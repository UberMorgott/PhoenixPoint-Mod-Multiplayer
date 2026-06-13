# Host-Authoritative Geoscape State Replication (SD-AIDR)

> **"Slaved-Clock Spectator-Drive with Authoritative InstanceData-Diff Replication"**

- **Status:** design spec (decompile-verified facts, readiness YELLOW; residual unknowns need 2-client in-game checkpoints).
- **Supersedes** the per-action StartTravel intercept model (see "Problem & rejected model").
- **Scope:** geoscape/campaign-layer state replication only. Tactical layer unchanged.

---

## 1. Problem & rejected model

- **Rejected: per-action curated intercept** (sync `StartTravel`, then the next action, then the next…) is a crutch:
  - Whack-a-mole — always misses some mutation (base attacks, prices, faction traffic, random events) → desync.
  - **PROVEN failure:** host-created vehicle absent on client (`"vehicle N not found"`); client runs its own divergent sim.
- **Root cause:** PP geoscape is non-deterministic — float math, frame-coupled time, local `UnityEngine.Random` → lockstep impossible.
- **Only correct model:** single authoritative sim on HOST + CLIENT as a pure mirror.

---

## 2. Architecture (summary)

Host is the **sole simulator**. Client keeps its geoscape scene **LOADED** (from the proven join reload) and runs **ZERO** stochastic simulation. Client clock is **slaved-AND-advancing** via the event-free seam `Timing.RecordInstanceData`/`ProcessInstanceData` (TimeBridge over `0x34`/`0x01`, already live) so the engine's own clock-driven DISPLAY coroutines render the living world locally (vehicle Slerp/rotation/ETA fills) at native framerate with **ZERO position bytes** (position = pure function of path + per-segment `startTime` + slaved clock). All genuinely stochastic state (deployment progress, prices, ownership, random-event outcomes, spawns) originates on the host and reaches the client as a per-ENTITY native **InstanceData-DIFF** stream + reliable entity create/destroy ops. Input forwards through the single `GeoAbility.Activate` funnel + base/roster seams into the existing **CommandSync** pipeline (host = sole mutator, last-writer-wins). Divergence is caught by a rolling **CRC32** over the host's serialized authoritative subset, self-healed by the proven two-barrier reload.

---

## 3. Verified load-bearing seams (decompile-confirmed; readiness YELLOW)

| Seam | Confidence | Detail |
|---|---|---|
| **CLOCK** | CONFIRMED C1 | `Timing.ProcessInstanceData` (`Base.Core/Timing.cs:222-232`) writes private backing fields, fires **ZERO** events (events only in `Scale` setter `:95` / `Paused` setter `:126-127`, not reachable). Skips `RescheduleUpdateables` (benign — scale applies lazily via `OwnNow` getters). Wrappers `TimeBridge.RecordHostState:85-103` / `ApplyTimeState:107-122`. Re-pin each `0x34`/`0x01` packet (~2-5Hz); client ticks at host scale between packets. |
| **LOCAL TRAVEL RENDER** | CONFIRMED C3/C5 | `GeoNavComponent.NavigateRoutine` (`GeoNavComponent.cs:94-151`): pos `Vector3.Slerp(Start,End, Ratio01(startTime,Now))` `:117`, PIVOT rotation `:119`, `RangeRemaining` `:124` — all clock-pure, RNG-free. `startTime = Actor.Timing.Now` re-captured **PER segment** `:106`; 5s preamble `:97`. Cosmetic sprite-banking heading `UpdateHeading` `LerpAngle` `:255` is frame-smoothed (**NOT** bit-identical, cosmetic only, self-converging — do **not** claim deterministic). |
| **ENTITY LIFECYCLE** | CONFIRMED C7/C18 | Vehicles born via `GeoFaction.CreateVehicle:2011-2026` / `CreateVehicleAtPosition:2028-2043` (Instantiate → `++_lastVehicleIndex` field `GeoFaction.cs:73` → `DoEnterPlay` → `VehicleAdded` `:2023`/`:2041`), **ENTIRELY outside InstanceData**. `_lastVehicleIndex` also bumps on `TakeOverVehicle:2049`. Map marker comes from the ENTITY lifecycle (`DoEnterPlay`/`Initialize` builds visuals BEFORE `VehicleAdded`) — replay must run `Initialize`/`DoEnterPlay`; do **NOT** gate `VehicleAdded` to control the marker. |
| **INPUT FUNNEL** | CONFIRMED C9 | `GeoAbility.Activate(GeoAbilityTarget)` (`GeoAbility.cs:130`) non-virtual single funnel for all 18 concrete subclasses → abstract `ActivateInternal:219` (call `:149`). One Harmony prefix on the `GeoAbilityTarget` overload intercepts every map/site intent (Move/Scan/Excavate/ExploreSite/LaunchMission/HavenTrade/Marketplace/Intercept/…). |
| **RELOAD BACKSTOP** | CONFIRMED in-game C14 | `SaveTransferCoordinator` full reload: `ReadSavegameBinary` (game `SerializationComponent.cs:280`, called `:241`), 32KB `SaveChunk 0x18` / `SaveDone 0x19`, `CRC32 :961-984`, `CreateSceneBinding :469`, `FinishLevel :664`, two-barrier `RevealAll`, entry `HostStartSession :196`. **RELIABLE only over DirectIP/Steam; STUN/UDP best-effort** (no ACK/retransmit). |
| **CampaignActionType enum** | CONFIRMED C10 | `enum CampaignActionType : byte` 0..14 @ `MessageSerializer.cs:556-573` (`StartTravel=13`, `SetTimeState=14`). MOD-defined. Append new intents **>= 15**. |
| **CRC32 reuse** | PARTIAL C17 | `SaveTransferCoordinator.Crc32(byte[]):978` folds arbitrary `byte[]` — reusable, BUT must fold the engine-Serializer image (`MemoryStream`) of each `RecordInstanceData` DTO; ONLY `[SerializeMember]`/`SerializeMembersByDefault` fields trip the hash — non-serialized runtime fields escape. **"Serialized state only," not "all runtime fields."** |

---

## 4. Client suppression — corrected

> **C2 was REFUTED:** there is **no single stochastic producer**.

Single Harmony patch `ClientGeoSimSuppressPatch` (**client-only**) over the **CLOSED, finite** producer set (prefix returns `false` / reschedule `NextUpdate.Never`). The set is bounded by engine code (**13 entries**), unlike the open-ended event space. **Best-effort by design** — backstopped by host state-diff + CRC reload (a missed producer = bounded local jitter, self-healed). All use the global `UnityEngine.Random` (**no** per-level seeded RNG in geoscape; `GeoMission` seeds tactical only).

### 4.1 Producer set (suppress on client)

| Class | Coroutine | Timing.Start | Mutates | RNG |
|---|---|---|---|---|
| GeoLevelController | LevelHourlyUpdateCrt | `GeoLevelController.cs:761` | income/recruits/pandoran engagement/aircraft engage + `HourTicked:832` | yes |
| VehicleFactionController | RescheduleDestinationCrt | `:123`/`:180`/`:235` | faction-ship destination + `StartMoving:185` (interval `SiteWaitHours.RandomValue:120`/`178`) | yes |
| GeoScavengingSite | RefreshEnemyAtSiteCrt | `:96`/`:135` | site enemy tags `WeightedRandomElement:76`/`79` | yes |
| GeoAlienBase | ExpandAlienBase | `:154`/`:479` | spawn `TacCharacter GetRandomElement:251`, reveal roll `Random.Range:279`, `_baseAttacksCounter` | yes |
| GeoScanner | Expand | `:135` | Range expansion → site reveal (`CompleteScan:84`) | no (clock) |
| SiteSurroundingsScanner | ExpandSiteScanner | `:213` | Range → alien-base reveal | no (clock) |
| GeoAncientSiteProbe | CompleteScanCrt | `:77` | `RevealSite:59`, `Range:70` | no (clock) |
| MistRepeller | ExpansMistRepeller | `:99` | mist-repeller range | no (clock) |
| GeoBehemothActor | SubmergeCrt | `:300`/`:575` | `CurrentBehemothStatus`, `_nextActionHoursLeft RandomValue:640` | yes |
| GeoBehemothActor | EmergeCrt | `:706` | `CurrentBehemothStatus`, move roll `Random.Range:778`, target `GetRandomElement:691`/`1010` | yes |
| GeoVehicle | SiteExplorationCompleted (via `ExploreCurrentSite:1138`→`:440`) | `:440` | `SiteExplored.Invoke`, `EndExplore` | no (clock) |
| GeoHarvestingSite | ResourceHarvestedCompleted | `:112`/`:166` | resource grant | no (clock) |
| MistRendererSystem | UpdateMist | `:201` | `MistData` (serialized) `_hoursPassed++` | no (clock) |

### 4.2 Whitelist (KEEP alive on client)

- `GeoNavComponent.NavigateRoutine` (display).
- `MistRendererSystem.FrameUpdate:205`.
- `GeoscapeLog.ProcessQueuedEvents:67`.
- Cosmetic frame coroutines.

### 4.3 Rejected suppression strategies (record why)

- **(ii) gate `Timing.Start`** — coroutine identity is a compiler-generated `<Method>d__N` state-machine, not cleanly knowable; `Start` is engine-wide → over-suppresses.
- **(iii) gate `UnityEngine.Random`** — native static, not cleanly patchable, corrupts tactical/UI RNG, AND insufficient (clock-driven non-RNG mutations still diverge).

---

## 5. Travel emitter suppression — corrected

> **C4 was REFUTED:** three emitters, not one.

When the client renders local travel, suppress **THREE** authoritative emitters (or run `NavigateRoutine` host-authoritative):

1. `Arrived?.Invoke` (`GeoNavComponent.cs:147` → `GeoVehicle OnArrived:315` → `CurrentSite.VehicleArrived:336` + `ArrivedAtDestinationEvent:347`).
2. `InitiateTravelling()` (`:108` → `TravelStartedEvent GeoVehicle.cs:590`).
3. `Travelling=true` setter (`:96`/`:102` → `VehicleLeft` + `CurrentSite=null GeoVehicle.cs:214-216`).

> Latter two are on `GeoVehicle` → **different mechanism** than nulling the `Arrived` delegate.

---

## 6. Launch loops — corrected

> **C15 was REFUTED:** player-originated, not host-only.

Interception + tactical launch are **PLAYER/UI-originated**, mod patches NONE → client can locally originate authoritative interception/tactical. Must **gate on client** (route to host):

- **Interception:** `Update:1004-1018` → `GeoscapeView.cs:774` → `LaunchInterceptionGame:1206` → `StartInterceptionCrt:1209` → `InterceptionGame.StartMission:1245`.
- **Tactical:** `GeoscapeView.LaunchMission:993` → `LaunchTacticalGameCrt:1410`.
- `GameTiming = Timing.ParentTime:231` (these run on the **parent clock**, unaffected by geoscape pause).

---

## 7. Transport (reuse existing infra; new packets)

| Packet | Status | Reliability / rate | Payload |
|---|---|---|---|
| `0x34`/`0x01` **TimingState** | EXISTING (time-sync Increment-1) | clock heartbeat ~2-5Hz | slaved-clock re-pin |
| `0x35` **GeoStateDiff** | NEW | unreliable, <=5Hz | host snapshots each live entity's native `RecordInstanceData` at tick start, diffs at tick end, ships ONLY changed entities' native blob (or scalar sub-delta for large `GeoFaction`). Idle hour ~0 bytes beyond clock. Client applies via native `ProcessInstanceData` under a **replication-suppress scope** (mirror `CommandRelay.IsApplying`) so field-writes reuse the native serializer/setter while event re-wiring + coroutine reschedule are gated. |
| `0x36` **GeoEntityOp** | NEW | reliable, ordered | create/destroy from host postfixes on `GeoFaction.CreateVehicle`/`CreateVehicleAtPosition` (`VehicleAdded`), `VehicleRemoved`, site add/remove; carries new-travel path + host `startTime`. Replay runs native entity `Initialize`/`DoEnterPlay` lifecycle. |

### 7.1 Marketplace (C16)

- **NO `ProcessInstanceData`** — restore = field-set `Marketplace.InstanceData` + `LevelStartLoadedGame` (`GeoMarketplace.cs:107-157`).
- `UpdateOptions:218-232` rebuilds the whole RNG array (new `GeoEventChoice` identities).
- **Replicate the WHOLE reliable blob;** client cannot re-derive (`UnityEngine.Random`).

### 7.2 HavenDefense progress (C6)

- `GeoHavenDefenseMission.ProcessInstanceData:181` (writes `:185-186` `AttackerDeployment`/`DefenderDeployment` backing fields), reached via `InstanceData` setter `:88-91`.
- `MissionProgress:47` = `a/(a+b)`, each `ceil(backing/100)` → circle moves in ~100-pt steps.
- Read-only `Friendly*` getters `:69`/`:73` — do **NOT** poke them.

---

## 8. InstanceData APPLY gotchas (C11/C12)

- **Clear-before-Add** (else duplicates) for:
  - `GeoSite._tacUnits` (`GeoSite.cs:1578`, List).
  - `GeoHaven.StockedResources` (`:1535`, `ResourcePack` append).
  - `GeoHaven._zones` (`:1542`, List).
- `GeoSite._addons` (`:1573`) is a **HashSet** — idempotent, **skip**.
- `GeoFaction._phoenixBaseTargets`/`_factionTags` already Clear-before-Add in `LevelStartLoadedGame:618-619`/`626-627`.
- `GeoVehicle.ProcessInstanceData` reschedules ONLY `ExploreCurrentSite:1138`→`Timing.Start(SiteExplorationCompleted):440` (gated `:1136`) — **guard it** (Stop prior before re-Start). `AddWeapons:1104`/`RepairHitPoints:1109` are synchronous, schedule nothing.
- `_expiringTimerAt` direct write `GeoSite.cs:1583` bypasses `RefreshVisuals` (it's the `GeoSiteTimerVisualController` countdown label, **NOT** the scanner circle — doc-only nuance).

---

## 9. netId (C8 corrected)

- **Namespaced id** (`veh:N` / `site:N`) — `VehicleID` (`GeoVehicle.cs:51`) and `SiteId` (`GeoSite.cs:45`) are independent counters/namespaces; keep `GeoBridge` two-path resolve.
- Forcing host `VehicleID` is **NOT** collision-free → reconcile/clamp per-faction `_lastVehicleIndex` (`GeoFaction.cs:73`); no engine uniqueness guard.
- **NOTE:** `GeoBridge.SiteId` is a static string METHOD (`GeoBridge.cs:77`); the int field is `GeoSite.SiteId`.

---

## 10. Acceptance criteria

The synced living world the client must show, matching host:

1. **Aircraft flight:** host order → flies on client, same path + arrival time (original `StartTravel` desync gone).
2. **Host-created entity** appears on client (vehicle-not-found gone).
3. **Base-defense progress circle** (who is winning) matches host.
4. **Faction base-to-base traffic** mirrors host.
5. **Marketplace prices** change in lockstep.
6. **Random events/encounters** fire identically (host-authoritative), no client-only/divergent rolls.
7. **Both players can issue commands** (client inputs → host, last-writer-wins); no client action mutates client engine directly.

---

## 11. Phased rollout

Each increment in-game verifiable; commit to inner `main` per increment.

| Inc | Scope | Fixes |
|---|---|---|
| **INC 1** — Client inert + slaved-clock travel mirror | `ClientGeoSimSuppressPatch` (13 producers) + 3 travel-emitter suppression (C4) + verify existing `0x34` clock drives advancing display. | #1 (baseline vehicles fly in sync) + stops client double-sim divergence. **[the user's CURRENT bug]** |
| **INC 2** — Entity lifecycle `0x36` GeoEntityOp | host-created vehicles/sites appear/despawn on client. Replay via native `Initialize`/`DoEnterPlay`. | #2 |
| **INC 3** — Authoritative state diff `0x35` GeoStateDiff (generic InstanceData-diff) + marketplace blob + havendefense progress | covers generically (incl. unknown events). | #3/#4/#5/#6 |
| **INC 4** — Input generalization | `GeoAbility.Activate` prefix → CommandSync (18 intents) + base/roster seams + launch-loop gating (C15). Retire per-action `StartTravel` patch. | #7 |
| **INC 5** — Divergence detection | rolling CRC32 over serialized authoritative subset → low-freq resync + two-barrier reload backstop. | — |

---

## 12. Residual unknowns (require in-game 2-client verification — user checkpoints)

- **C14** reliable only DirectIP/Steam; STUN best-effort.
- Whether the new lower-choke suppression **actually eliminates divergence** (2-client diff).
- **C8** collision behavior runtime-only.
- **C3** sprite-banking micro-divergence eyeball-only.
- **C6** ~100-pt step granularity of the defense circle.
- **C12/C13** double-exploration / timer re-arm runtime-only.
