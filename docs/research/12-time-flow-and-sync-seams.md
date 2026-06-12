# Geoscape Time-Flow API & Host-Authoritative Clock Sync Seams

> GROUNDING for **Stage 2: host-authoritative time/clock sync** of the Multipleer co-op mod
> (`docs/superpowers/specs/2026-06-12-geoscape-command-sync-design.md` §Stage 2; design goals in
> [08-geoscape-concurrency](08-geoscape-concurrency.md), permission bit in [03-campaign-layer](03-campaign-layer.md)).
> Resolves the OPEN SDK question "Time-flow API (pause/speed)" that gated Stage 2.
> All game refs: decompiled DLL tree `decompiled\AssemblyCSharp\Assembly-CSharp\src` (FALLBACK proxy per
> `docs/research/source-provenance.md`; confirm exact site against installed `Assembly-CSharp.dll` before hooking).
> Mod refs: `Multipleer\src`. Date 2026-06-13.

## 1. Clock owner

- **Authoritative geoscape clock = `GeoLevelController.Timing`** (type `Base.Core.Timing`).
  - `PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:229` — `public Timing Timing { get; private set; }`
  - `:231` — `public Timing GameTiming => Timing.ParentTime;`
  - `:309` — `public TimeUnit ElaspedTime` (sic); `:233` `DateTime? TimeLimit`.
- **`Base.Core.Timing`** = `Base.Core\Timing.cs:10-393`. A *hierarchical* scaled clock (parent→child chain), NOT a stored timestamp.
  - `Now` (`:55`) `=> StartTime + OwnNow` — **DERIVED**, integrated each frame from the parent clock; no stored "current time" field.
  - `OwnNow` (`:172`, private) `= _ownSetTime + (_paused ? 0 : (ParentOwnNow - _parentSetTime) * Scale)` — rebase-point integration.
  - Root of the chain = real Unity time: `ParentOwnNow` (`:176`) falls back to `TimeUnit.FromSeconds(Time.time)` when `ParentTime==null`.
  - `Delta` (`:59`) `=> ParentDelta * Scale`; `EffectiveScale` (`:67`) `= Paused ? 0 : CumulativeScale`.
  - Fields: `_scale` (`:21`, default 1f), `_paused` (`:23`), `_ownSetTime`/`_parentSetTime`/`_ownSetFixedTime`/`_parentSetFixedTime` (`:24-31`, private rebase anchors), `_parentTiming` (`:33`).
- **Tick driver = per-instance Unity loop** (frame-rate-coupled): root `Base.Core/TimeComponent.cs` (`[DefaultExecutionOrder(-155)]`), `Timing { get; } = new Timing()` (`:12`); `Awake` (`:14`) if `RootTime` starts `TimingScheduler` + two coroutines `UpdateScheduler(Regular)` / `UpdateScheduler(Fixed)` (`:34-42`) pumping `Timing.Scheduler.Update(phase)` every Unity frame / `WaitForFixedUpdate`. Geoscape `GeoLevelController.Timing` is a CHILD whose `ParentTime` chains to this root real-time clock → **each instance advances its own geoscape time off its own frame loop.** This is the root desync.
- Hourly sim driver: `GeoLevelController.LevelHourlyUpdateCrt(Timing)` `:777-834`, rescheduled via `NextUpdate.After(TimeUtils.GetNextHour(Timing))`; fires `HourTicked?.Invoke(...)` (`:832`). Events: `HourTicked` (`:339`), `DailyUpdateEvent` (`:341`).

## 2. Pause / resume API

- **`Timing.Paused`** property — `Base.Core\Timing.cs:100-130`.
  - getter: `true` if any ancestor paused (`ParentTime.Paused`) else `_paused`.
  - setter: on change rebases anchors, then fires **`EffectiveScaleChangedEvent`** AND **`OnPausedEvent(this,_paused)`** (`:126-127`).
- UI pause button → `PhoenixPoint.Geoscape.View.ViewModules/UIModuleTimeControl.cs`:
  - `PauseTimeButton` click → `OnPauseTimeKeyPressed` (`:174`) → `OnPauseTime(!_timing.Paused)` (`:179`).
  - `OnPauseTime(bool)` (`:183-191`) → `_timing.Paused = pause` (`:188`) + fires public event **`OnTimePauseChangeRequested(pause)`** (`:190`).
- Central app-level pause gate: `GeoscapeView.SetGamePauseState(bool)` `GeoscapeView.cs:1271-1281` → `timing.Paused = paused` (`:1279`) (guards against unpausing past `TimeLimit`).

## 3. Speed / time-scale API

- **`Timing.Scale`** property — `Base.Core\Timing.cs:79-98`. Setter rebases anchors + fires `EffectiveScaleChangedEvent` (`:95`). `Scale` is the raw float multiplier (game-seconds per real-second basis).
- UI speed buttons → `UIModuleTimeControl.cs`:
  - `IncreaseTimeButton`/`DecreaseTimeButton` clicks → `OnTimeIncrease`/`OnTimeDecrease` (`:204/:212`) → `ChangeTime(bool)` (`:225`) → `SelectTimePreset(int)` (`:193-202`).
  - `SelectTimePreset` clamps index, sets `SelectedPresetTime`, calls `UpdateSelectedTime` (`:271-275`) → `_timing.Scale = PresetTimes[SelectedPresetTime]` (`:273`) + fires public event **`OnTimeSpeedChangeRequested(SelectedPresetTime)`** (`:200`).
  - `PresetTimes` (`:21`, `float[]`) = the in-game 1x/fast/faster scale values; `SelectedPresetTime` (`:24`, default 1); `SelectedTimeScale => PresetTimes[SelectedPresetTime]` (`:82`).
  - `Init(timing,...)` (`:101`) subscribes `_timing.OnPausedEvent += TimingOnPausedEvent` and snaps `SelectedPresetTime` to whichever preset matches current `Scale`.
- Console (host-only debug reference): `geo_speed` → `GeoLevelController.SetSpeed(IConsole, float)` `:1685-1689` → `Timing.Scale = scale * 300f` (so console arg is in "×300" units; the UI presets are raw `Scale`, not ×300 — do NOT conflate).

## 4. Auto-pause sites (host must broadcast these)

- **Single funnel = `GeoscapeView.RequestGamePause()`** `GeoscapeView.cs:1284-1290` → coroutine `RequestPauseCrt` (`:1305-1311`) → `SetGamePauseState(paused:true)` (`:1308`) → re-syncs UI via `TimeControlModule.SetTimeState` (`:1310`).
- Callers (auto-pause triggers; `find_referencing_symbols GeoscapeView/RequestGamePause`):
  - **Vehicle arrival at site** — `UIStateVehicleSelected.OnVehicleArrived` `:1218-1238` (→ `RequestGamePause` `:1224`).
  - Vehicle site explored/excavated — `UIStateVehicleSelected.OnVehicleSiteExplored:1183` / `OnVehicleSiteExcavated:1188`; `UIStateNothingSelected.OnVehicleSiteExcavated:603`; site visited / launch-mission `OnVehicleSiteVisited:1307`.
  - **Geoscape event-log popup** — `GeoscapeView.OnLogNewEntry:1648`; vehicle-needs-attention `VehicleNeedsAttention:1657`; base activated `PxFaction_OnBaseActivated:2040`.
  - Mission launch — `GeoLevelController.LaunchTacticalGameCrt:1416`; game over `UIStateGameOver.EnterState:14`; asset deployment `UIStateAssetDeployment:59`; cutscene `GeoscapeView.ToCutsceneState:672`; haven detail `:728`; edit-unit `:506`.
  - Menu-open pauses (Research/Manufacturing/BaseLayout/Diplomacy/GeoscapeLog/Options/AbilityTarget/Replenish `EnterState`s) — these are *local view* pauses; per [08](08-geoscape-concurrency.md) the VIEW is not synced, so most of these should stay LOCAL. Only **decision/global** pauses (vehicle arrival, event popup, mission launch, base assault) need host broadcast. Curate the list — do NOT blindly sync every `RequestGamePause`.
- Lifecycle force-pauses (`Timing.Paused = true`): `GeoLevelController.LevelCrt:569,611`, `OnLevelEnd:493`; save/load `GeoscapeView.SaveGameCrt:1147`, `LoadGameCrt:1172`.

## 5. Settability seam — force client clock = host

- **`Now` is DERIVED, not settable directly.** `StartTime` IS public-settable (`Timing.cs:51 { get; set; }`) but the rebase anchors `_ownSetTime`/`_parentSetTime` are private → you cannot make client `Now` equal host `Now` by poking one public field.
- **Use the save/load seam = `RecordInstanceData()` / `ProcessInstanceData(TimingInstanceData)`** — the game's own authoritative clock-set path.
  - `Timing.RecordInstanceData()` `Timing.cs:209-220` → returns `TimingInstanceData{ Paused, Scale, StartTime, StartFixedTime, OwnNow, OwnFixedNow }`.
  - `Timing.ProcessInstanceData(data)` `Timing.cs:222-232` → writes `_paused, _scale, StartTime, StartFixedTime, _ownSetTime=OwnNow, _ownSetFixedTime=OwnFixedNow` and re-anchors `_parentSetTime/_parentSetFixedTime` to current parent → **fully forces the clock**.
  - `TimingInstanceData` = `Base.Core\TimingInstanceData.cs:4-18`, `[SerializeType(SerializeMembersByDefault=SerializeOwn)]`, public fields (already serializable by the game serializer; matches `04-serialization`).
  - PROOF it is the load seam: `GeoLevelController.LevelCrt:516` `Timing.ProcessInstanceData(instanceData.TimingData)` on save-load.
- **Recommended client-side model** (combine):
  1. **Suppress client clock advance** — on each client, hold `Timing.Paused = true` (or `Scale = 0`-equivalent) so the client never integrates its own geoscape time, AND suppress the local hourly sim (see §6 risk).
  2. **Mirror host clock** — host periodically broadcasts `RecordInstanceData()` (or just `{Paused, Scale, Now}`); client applies via `ProcessInstanceData` so displayed `Now`/`Scale`/`Paused` match.
  - Alternative (heavier): drive client ticks from host (host broadcasts each hour-tick / delta); avoids local integration entirely but needs the hourly sim to be host-only anyway (§6).

## 6. Hook points / events for interception & broadcast

- **Client interception (block local, send to host):** Harmony-prefix the WRITES, or subscribe the request events:
  - Pause: prefix `Base.Core.Timing.set_Paused` OR `UIModuleTimeControl.OnPauseTime` / subscribe `UIModuleTimeControl.OnTimePauseChangeRequested`.
  - Speed: prefix `Base.Core.Timing.set_Scale` OR `UIModuleTimeControl.UpdateSelectedTime`/`SelectTimePreset` / subscribe `OnTimeSpeedChangeRequested`.
  - Auto-pause: prefix `GeoscapeView.RequestGamePause` / `SetGamePauseState` (host emits; on client suppress local, await host).
  - NOTE: prefixing `set_Paused`/`set_Scale` directly is the most robust (covers UI + console + lifecycle) but also catches LOCAL view-menu pauses — gate by "is this a synced/global pause?" to avoid yanking each other into menus.
- **Host broadcast (notify after change):** subscribe `Timing.OnPausedEvent` (`:188`) and `Timing.EffectiveScaleChangedEvent` (`:186`) to detect host clock changes, then broadcast time-state. `OnResetEvent` (`:190`) on `Timing.Reset()`.
- **Existing mod CommandSync seams to plug a `TimeControl` action into** (reuse Stage-1 pipeline, gated by `ControlTime = 1<<7`):
  - Add `CampaignActionType` value(s) — `src\Network\MessageLayer\MessageSerializer.cs:533-549` (enum currently 0..13 `StartTravel`; append e.g. `SetTimePaused`, `SetTimeScale`).
  - `PermissionGate.RequiredPermission` `src\Network\CommandSync\PermissionGate.cs:12-39` → map new types → `CampaignPermission.ControlTime` (bit defined `src\Validation\PermissionManager.cs:18`). Mirror in `ActionValidator.GetRequiredPermission` `src\Validation\ActionValidator.cs:69-92`.
  - `InterceptRegistry._entries` `src\Network\CommandSync\InterceptRegistry.cs:22-45` → add row `{ ActionType, RequiredPermission=ControlTime }`.
  - Client prefix patch mirroring `src\Harmony\StartTravelInterceptPatch.cs` (encode `CampaignActionRequest` → `SendToHost` → return false to block local write). Pattern: `:50` `ActionType=...`, host-origin path `:75-80` `engine.BroadcastCampaignActionResult`.
  - Host: `HostArbiter.HandleRequest` `src\Network\CommandSync\HostArbiter.cs:24-55` already does GUID resolve → `PermissionGate.IsAllowed` → `_relay.ApplyResult` → `BroadcastCampaignActionResult` — **no change needed**; new action type flows through unchanged once registered.
  - Apply branch: `CommandExecutor.Execute` switch `src\Network\CommandSync\CommandExecutor.cs:13-24` → add `case SetTimePaused/SetTimeScale:` setting `GeoLevelController.Timing.Paused/.Scale` (under `CommandRelay.IsApplying` guard so the client prefix lets the re-entrant write through, exactly like `ApplyStartTravel`).
  - Broadcast plumbing exists: `NetworkEngine.BroadcastCampaignActionResult` `:288-293` (routes via `0x31 CampaignActionApproved` → `OnHostCampaignActionResult`). `CampaignActionResult 0x33` / `CampaignStateUpdate 0x34` (`PacketType.cs:47-48`) remain STUBS (`NetworkEngine.cs:530-535` TODO) — Stage 2 can keep using the result-replay path (0x31) like Stage 1, OR define `0x34 CampaignStateUpdate` payload = serialized `TimingInstanceData` for the periodic full-clock mirror (`BroadcastCampaignState` `:295-300` already wraps it; the receive `case CampaignStateUpdate` `:534` is the TODO to implement).

## 7. Open questions / risks

- **R1 — Mirroring the displayed clock is INSUFFICIENT (biggest risk).** `LevelHourlyUpdateCrt` `:777-834` runs heavy AUTHORITATIVE local sim every game-hour off the LOCAL `Timing`: resource income, research progress (`UpdateResearch`), manufacturing (`Manufacture.Update`), recruit generation (`GenerateRecruits`), faction AI/alien hourly (`UpdateFactionHourly`), aircraft repair, Pandoran engagement/interception. If both instances keep ticking, these run twice and DIVERGE even with a synced display. Stage-2 MUST either (a) hard-suppress the client's hourly loop (e.g. keep client `Timing.Paused` so `LevelHourlyUpdateCrt` never reschedules) and let host broadcast the resulting state deltas/results, or (b) drive client purely from host ticks. Plain "set client `Now` = host `Now`" does not stop local sim.
- **R2 — Frame-rate coupling.** Time advance integrates off Unity `Time.time`/`Time.deltaTime` per-frame (`Timing.cs:176-182`, `TimeComponent` coroutines). Two instances at different FPS drift continuously between sync points → need frequent host clock broadcasts (or full client-tick suppression per R1).
- **R3 — Event RNG / vehicle-movement determinism.** Vehicle travel integration, geoscape event firing, recruit/loot RNG all advance on the same local `Timing`. Per the design ([08](08-geoscape-concurrency.md), `specs/01`: "no client-side RNG"), events must fire on HOST only and replicate as results — confirm geoscape event generation is host-gated in Stage 3 ([08](08-geoscape-concurrency.md) §6 open: event-generation hook points). If client clock still advances locally, it will independently roll events → divergence.
- **R4 — View vs global pause separation.** `RequestGamePause` is called by BOTH global triggers (vehicle arrival, event popup) and purely-local menu opens (Research/Manufacturing/Options `EnterState`). Per [08](08-geoscape-concurrency.md) the view is NOT synced. Curate which pause sites broadcast; a blanket prefix on `set_Paused` would force everyone into a menu pause. Recommend hooking the specific decision/global sites (`OnVehicleArrived`, `OnLogNewEntry`, mission launch, base assault) for broadcast, and keep menu pauses local.
- **R5 — `TimeLimit` unpause guard.** `SetGamePauseState` `:1273` refuses to unpause if `Now.DateTime >= TimeLimit`. A forced client `ProcessInstanceData` could land the client at/over `TimeLimit` while host is below it → mismatched pause acceptance. Apply host time-state directly via `Timing.ProcessInstanceData`, not via `SetGamePauseState`, to bypass this client-side guard.
- **R6 — Parent-chain pause.** `Timing.Paused` getter returns `true` if any ANCESTOR is paused (`:104`). The geoscape clock's parent is the root real-time clock; confirm no unintended parent pause (e.g. app-focus-loss) is read as a game pause to broadcast.
- **R7 — `geo_speed` ×300 vs UI raw-Scale mismatch.** Encode/decode time-scale by the UI preset INDEX (`SelectedPresetTime`) or the raw `Scale` value consistently; do not mix the console's `×300` convention. Safest payload = `SelectedPresetTime` index (clients hold identical `PresetTimes[]` from the same defs) + an explicit `Paused` bool.
- **R8 — Verify against runtime DLL.** Line numbers are decompile-proxy (`source-provenance.md`); confirm `Timing.set_Paused`/`set_Scale`/`ProcessInstanceData` and `UIModuleTimeControl` event names against the installed `Assembly-CSharp.dll` before patching (`specs/03` rule).

## Cross-refs
- Design goals / last-writer-wins / `TIME_STATE`: [08-geoscape-concurrency](08-geoscape-concurrency.md).
- `ControlTime` permission bit + enforcement seam: [03-campaign-layer](03-campaign-layer.md) §Permissions, `PermissionManager.cs:18`.
- Stage map + reused infra: `docs/superpowers/specs/2026-06-12-geoscape-command-sync-design.md` (Stage 2).
- Serialization of `TimingInstanceData`: [04-serialization](04-serialization.md).
