# Time Sync — Stage 2 Increment 1 (Host-Authoritative Time) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. TDD, DRY, YAGNI, frequent commits — commit straight to `feat/geoscape-command-sync`, tests-green before each commit.

**Goal:** Make the geoscape **clock host-authoritative**. Client pause / resume / speed-change inputs are intercepted on the client, blocked locally, and sent to the host as a `SetTimeState` command. The host owns the only running clock+sim: it applies the request (gated by `ControlTime`), broadcasts the authoritative timing, and the client **mirrors** it. The client never advances its own authoritative geoscape sim (R1 double-sim is killed by skipping the client's `LevelHourlyUpdateCrt` body), and host auto-pause (vehicle arrival / event popup / mission launch) propagates to the client. Increments 2 (world-state delta broadcast) and 3 (route base/research/manufacture/squad inputs) are **future plans, out of scope here**.

**Architecture:** Host-authoritative thin-client (fixed; not re-litigated — PP is non-deterministic so lockstep is unviable). Extends the existing Stage-1 CommandSync pipeline unchanged: a new `CampaignActionType.SetTimeState` flows through the SAME `Harmony prefix → CampaignActionRequest 0x30 → HostArbiter (PermissionGate ControlTime) → CommandRelay.ApplyResult → BroadcastCampaignActionResult 0x31` path used by `StartTravel`. On top of the discrete command path, a continuous **clock mirror** runs: the host periodically (and on every time change / auto-pause) broadcasts its `Timing.RecordInstanceData()` over `0x34 CampaignStateUpdate`; the client applies it via `Timing.ProcessInstanceData`, forcing displayed `Now`/`Scale`/`Paused` to the host and correcting frame-rate drift (R2). The client's own hourly authoritative sim is hard-suppressed by a Harmony prefix on `GeoLevelController.LevelHourlyUpdateCrt` that skips the body but keeps the coroutine rescheduling so the displayed clock still advances.

**Tech Stack:** C# (net472), HarmonyLib (`AccessTools`/`Traverse` dynamic resolution — mod never hard-references game types), xUnit 2.9.2 (`Multiplayer.Tests`), existing `CampaignAction` packet skeleton (`0x30/0x31/0x32`) + `0x34 CampaignStateUpdate` (stub, implemented here), `NetworkEngine` campaign events + per-frame `Update()` driver (`MultiplayerMain.Update → NetworkEngine.Update`), `PermissionManager` per-GUID `ControlTime = 1<<7`.

---

## Grounding notes (verified against real source + decompile — read before coding)

### Mod (extend, don't reinvent) — `E:\DEV\PhoenixPoint\Multiplayer\src`
- **`CampaignActionType` enum** `src/Network/MessageLayer/MessageSerializer.cs:533-549` currently `0..13` (`StartTravel = 13`). Append **`SetTimeState = 14`**.
- **`CampaignActionMessage`** `:561-568` = `ActionId(Guid)`, `ActionType`, `TargetId(string)`, `Payload(byte[])`, `Timestamp(long)` — **no `ActorId`**. `SerializeCampaignAction`/`DeserializeCampaignAction` `:48-78` round-trip it; reused unchanged (time payload rides in `Payload`, `TargetId=""`).
- **Stage-1 pipeline to mirror:**
  - `src/Harmony/StartTravelInterceptPatch.cs` — `Prepare()/TargetMethod()/Prefix()/Postfix()` pattern: client→encode `CampaignActionMessage`→`CommandRelay.Instance.RelayFromClient(msg)`→`return false`; host→`return true` + postfix `engine.BroadcastCampaignActionResult(msg)`; both skip when `CommandRelay.IsApplying`.
  - `src/Network/CommandSync/InterceptRegistry.cs:22-45` — `_entries` dict; add a `SetTimeState` row (`RequiredPermission = ControlTime`, `SignatureConfirmed = true`; the engine method is resolved inside the executor, not by the registry resolver, so the row's `DeclaringTypeName/MethodName` describe the apply target for documentation/consistency).
  - `src/Network/CommandSync/PermissionGate.cs:12-39` `RequiredPermission` + `src/Validation/ActionValidator.cs:69-96` `GetRequiredPermission` — add `case SetTimeState: return ControlTime;` in BOTH.
  - `src/Network/CommandSync/HostArbiter.cs:24-55` `HandleRequest` — resolve GUID → `PermissionGate.IsAllowed` → `_relay.ApplyResult(action)` → `BroadcastCampaignActionResult`. **No change** (new type flows through once registered + executor branch exists).
  - `src/Network/CommandSync/CommandRelay.cs:65-83` `ApplyResult` — looks up registry, sets `[ThreadStatic] _applying`, calls `CommandExecutor.Execute`. The guard `CommandRelay.IsApplying` lets the host/client's own intercept prefix pass a re-entrant apply through.
  - `src/Network/CommandSync/CommandExecutor.cs:13-24` — `switch(action.ActionType)`; add `case SetTimeState: ApplySetTime(action);`.
  - `src/Network/CommandSync/ClientApplier.cs:26-37` `HandleResult` → `_relay.ApplyResult` (client reproduces approved action). No change.
  - `src/Network/CommandSync/CommandCodec.cs` — pure, Unity-free `BinaryWriter` codec (`StartTravelPayload` precedent). New payloads added here (stay pure → unit-tested).
  - `src/Network/CommandSync/GeoBridge.cs` — `AccessTools` id↔entity bridge; `GetGeoLevelController()` via `GameUtl.CurrentLevel().GetComponent<GeoLevelController>()`. Extend with Timing/module accessors (Unity-side, not tested).
- **`NetworkEngine`** `src/Network/NetworkEngine.cs`:
  - `BroadcastCampaignActionResult` `:288-293` (0x31 fan-out) reused unchanged.
  - `BroadcastCampaignState(byte[])` `:295-300` wraps `SerializeGameState("campaign",...)` into `0x34` — **NOT reused** (its framing is generic; this increment defines a typed timing path). `0x34 CampaignStateUpdate` **receive-case is a TODO** `:534-536` — implemented here.
  - `Update()` `:304-308` (`Transport/Session/SaveTransfer`) — host clock-mirror broadcaster ticked here; called every frame by `MultiplayerMain.Update():55-58`.
  - `RouteMessage` `0x34` case `:534` currently empty.
- **`CampaignPermission.ControlTime = 1<<7`** `src/Validation/PermissionManager.cs:18`; `HasCampaignPermission(Guid, perm)` honours `FullCommander` override `:88-97`. Unity-free → unit-linkable.
- **Patch registration:** `MultiplayerMain.OnModEnabled` `:24-25` does `harmony.PatchAll(Assembly.GetExecutingAssembly())` → **new Harmony patch classes auto-discover; no manual registration**.
- **Test linkage** `Multiplayer.Tests/Multiplayer.Tests.csproj` — `EnableDefaultCompileItems=false`; pure cores linked via `<Compile Include="..\src\...\X.cs"><Link>`. `MessageSerializer.cs`, `CommandCodec.cs`, `PermissionManager.cs`, `PermissionGate.cs`, `InterceptRegistry.cs` already linked. New pure codec types live in `CommandCodec.cs` (already linked → no csproj edit for the codec).
- **Main `Multiplayer.csproj`** has no `EnableDefaultCompileItems=false` / no explicit `<Compile>` → SDK globs `src/**/*.cs`. **New src files need no csproj edit.**

### Decompile signatures (verified `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src`)

| Symbol | Confirmed shape | File:line | Notes |
|--------|-----------------|-----------|-------|
| `Base.Core.Timing.Paused` | `public bool { get; set; }` — setter rebases anchors, fires `EffectiveScaleChangedEvent` + `OnPausedEvent(this,_paused)` | `Base.Core/Timing.cs:100-130` | getter returns `true` if any ancestor paused (R6) |
| `Base.Core.Timing.Scale` | `public float { get; set; }` — setter rebases, fires `EffectiveScaleChangedEvent` | `Timing.cs:79-98` | raw multiplier, **not** ×300 (R7) |
| `Timing.RecordInstanceData()` | `public TimingInstanceData RecordInstanceData()` | `Timing.cs:209-220` | returns `{Paused,Scale,StartTime,StartFixedTime,OwnNow,OwnFixedNow}` |
| `Timing.ProcessInstanceData(d)` | `public void ProcessInstanceData(TimingInstanceData)` | `Timing.cs:222-232` | writes `_paused,_scale,StartTime,StartFixedTime,_ownSetTime=OwnNow,_ownSetFixedTime=OwnFixedNow` + re-anchors parent. **Pokes private fields directly — fires NO events, calls NO reschedule** → safe authoritative mirror, no re-intercept |
| `Base.Core.TimingInstanceData` | public fields `bool Paused; float Scale; TimeUnit StartTime,StartFixedTime,OwnNow,OwnFixedNow` | `Base.Core/TimingInstanceData.cs:6-19` | `[SerializeType(SerializeOwn)]` |
| `Base.Core.TimeUnit` | `struct` wrapping `readonly TimeSpan _time`; public `TimeSpan TimeSpan { get; }`, static `TimeUnit FromTimeSpan(TimeSpan)` | `Base.Core/TimeUnit.cs:7-33` | wire-encode as `tu.TimeSpan.Ticks` (long); rebuild `TimeUnit.FromTimeSpan(TimeSpan.FromTicks(t))` — public path, no private `_time` reflection |
| `UIModuleTimeControl.OnPauseTime(bool pause)` | `private void` → `_timing.Paused = pause` + fires `OnTimePauseChangeRequested(pause)` | `…ViewModules/UIModuleTimeControl.cs:183-191` | funnel for pause button (`OnPauseTimeKeyPressed:174`) **and** `SetTimeState(bool):220-223`. Private → Harmony patches private fine |
| `UIModuleTimeControl.SelectTimePreset(int)` | `public void` → clamps, sets `SelectedPresetTime`, `UpdateSelectedTime()`, fires `OnTimeSpeedChangeRequested(SelectedPresetTime)` | `:193-202` | funnel for increase/decrease (`ChangeTime:225`). Public |
| `UIModuleTimeControl.UpdateSelectedTime()` | `private void` → `_timing.Scale = PresetTimes[SelectedPresetTime]` | `:271-275` | actual Scale write |
| `UIModuleTimeControl.PresetTimes` | `public float[]` | `:21` | preset scale table; `SelectedPresetTime` (`int`, default 1) `:24` |
| `UIModuleTimeControl._timing` | `private Timing` | `:68` | read via `Traverse` for current state in client prefixes |
| `GeoscapeView.SetGamePauseState(bool)` | `public void` → `timing.Paused = paused` (guards unpause past `TimeLimit`) | `…View/GeoscapeView.cs:1271-1282` | **single funnel** all auto-pause routes through (`RequestPauseCrt:1308`). TimeLimit guard `:1274` (R5) |
| `GeoscapeView.RequestGamePause()` | `public void` → coroutine `RequestPauseCrt():1305-1311` → `SetGamePauseState(true)` next frame | `:1284-1290` | deferred; patch `SetGamePauseState` (where `Paused` actually flips) not this |
| `GeoscapeView.GeoscapeModules` | `public GeoscapeModulesData` field | `:61` | `.TimeControlModule` = `public UIModuleTimeControl` field `Base.UI/GeoscapeModulesData.cs:14` |
| `GeoLevelController.LevelHourlyUpdateCrt(Timing timing)` | `private NextUpdate` — heavy hourly sim (income/research/manufacture/recruits/faction AI/interception), reschedules `return NextUpdate.After(TimeUtils.GetNextHour(Timing))` | `…Levels/GeoLevelController.cs:777-834` | **R1 double-sim source.** Started `:761`. Prefix-skip on client |
| `GeoLevelController.Timing` | `public Timing { get; private set; }` | `GeoLevelController.cs:229` | clock owner |
| `Base.Core.NextUpdate.After(TimeUnit)` | `public static NextUpdate After(TimeUnit)` | `Base.Core/NextUpdate.cs:93-96` | build the client reschedule result |
| `TimeUtils.GetNextHour(Timing)` | `public static TimeUnit GetNextHour(Timing)` | `TimeUtils.cs:17` | next-hour target for reschedule |

**No grounding gap:** every doc-12 symbol used below was re-verified against source. (Caveats only: doc-12 named two enum values `SetTimePaused`/`SetTimeScale`; this plan consolidates to ONE `SetTimeState` carrying both fields — see Design Decisions. Doc-12 line numbers for `Timing` were exact; `GeoLevelController`/`GeoscapeView`/`UIModuleTimeControl` lines all matched the live decompile.)

---

## Design Decisions (locked)

- **D1 — One action, both fields.** `CampaignActionType.SetTimeState = 14`. Payload = `Paused` (bool) **+** `PresetIndex` (int = `SelectedPresetTime`), NOT raw `Scale`/×300 (R7). A pause toggle or a speed change both send a full `{Paused, PresetIndex}` snapshot. Single enum value + single apply path avoids two-action ordering races; clients share identical `PresetTimes[]` from defs so the index is unambiguous.
- **D2 — Client intercept seams.** Pause → prefix `UIModuleTimeControl.OnPauseTime(bool)` (covers pause button + UI re-sync). Speed → prefix `UIModuleTimeControl.SelectTimePreset(int)` (covers increase/decrease). Both: client+active+!`IsApplying` → read current state via `Traverse`, send `SetTimeState`, `return false` (block local write). Host/`IsApplying` → `return true`.
- **D3 — Apply via the live module (coherent display).** `CommandExecutor.ApplySetTime` resolves the live `UIModuleTimeControl` (`GeoscapeView.GeoscapeModules.TimeControlModule`) and invokes `SelectTimePreset(index)` + `OnPauseTime(paused)` under `IsApplying`, so `SelectedPresetTime`, animator, and `Timing.Scale`/`Paused` all stay consistent. Fallback if the module is unreachable: poke `GeoLevelController.Timing.Paused`/`.Scale` directly.
- **D4 — Client sim freeze (R1) = prefix-skip `LevelHourlyUpdateCrt`.** On the **client only**, prefix returns `false` (skip the heavy authoritative body) but sets `__result = NextUpdate.After(TimeUtils.GetNextHour(timing))` so the coroutine stays alive and the displayed clock keeps advancing — **zero local authoritative sim**, host is the only simulator. Chosen over "hold client `Timing.Paused=true`" (which also freezes the display + travel integration and fights the mirror). **Seam: `GeoLevelController.cs:777` `LevelHourlyUpdateCrt`.**
- **D5 — Continuous clock mirror over `0x34`.** Host periodically (~0.5s) and immediately-after-change broadcasts `Timing.RecordInstanceData()`; client applies `Timing.ProcessInstanceData` → forces `Now`/`Scale`/`Paused`, corrects FPS drift (R2). `0x34` payload = `[subtype:byte][body]`; subtype `0x01 = TimingState` (future increments add `0x02 = WorldDelta`). Bypasses the `SetGamePauseState` TimeLimit guard (R5) because `ProcessInstanceData` sets fields directly.
- **D6 — Auto-pause propagation.** Host: **postfix** `GeoscapeView.SetGamePauseState(bool)` → `TimeSyncBroadcaster.BroadcastNow()` (the single funnel covers vehicle-arrival / event-popup / mission-launch). Client: **prefix** `GeoscapeView.SetGamePauseState(bool)` → `return false` (suppress independent local auto-pause; the host mirror drives the client clock). Menu/view-pause separation (doc-12 R4) is **deferred** — see Open Risks.

---

## File Structure

### New files (single responsibility)

| File | Responsibility | Pure / Unity-free? |
|------|----------------|--------------------|
| `src/Harmony/TimePauseInterceptPatch.cs` | Harmony prefix on `UIModuleTimeControl.OnPauseTime(bool)`: client → send `SetTimeState{Paused=arg, PresetIndex=current}` + `return false`; host/`IsApplying` → `return true`. | Engine/Harmony — build + 2-instance |
| `src/Harmony/TimeSpeedInterceptPatch.cs` | Harmony prefix on `UIModuleTimeControl.SelectTimePreset(int)`: client → send `SetTimeState{Paused=current, PresetIndex=arg}` + `return false`; host/`IsApplying` → `return true`. | Engine/Harmony — build + 2-instance |
| `src/Harmony/ClientHourlySimSuppressPatch.cs` | Harmony prefix on `GeoLevelController.LevelHourlyUpdateCrt(Timing)`: client-only → set `__result = NextUpdate.After(TimeUtils.GetNextHour(timing))`, `return false`. Host/SP → `return true`. | Engine/Harmony — build + 2-instance |
| `src/Harmony/AutoPauseSyncPatch.cs` | Harmony patch on `GeoscapeView.SetGamePauseState(bool)`: host **postfix** → `TimeSyncBroadcaster.BroadcastNow()`; client **prefix** → `return false`. | Engine/Harmony — build + 2-instance |
| `src/Network/CommandSync/TimeBridge.cs` | `AccessTools`/`Traverse` Unity-side helpers: resolve `GeoscapeView`→`TimeControlModule`, read `SelectedPresetTime`/`_timing.Paused`, invoke `SelectTimePreset`/`OnPauseTime`, `RecordInstanceData`→`TimeStatePayload`, `TimeStatePayload`→`ProcessInstanceData`. Keeps game types out of pure code. | Engine — build + 2-instance |
| `src/Network/CommandSync/TimeSyncBroadcaster.cs` | Host-only periodic clock mirror: ticked from `NetworkEngine.Update()`; throttle ~0.5s; `BroadcastNow()` for immediate push. Reads host `RecordInstanceData` via `TimeBridge`, calls `NetworkEngine.BroadcastTimingState`. | Engine — build + 2-instance |
| `src/Network/CommandSync/ClientTimeMirror.cs` | Client-only: apply a received `TimeStatePayload` via `TimeBridge.ApplyTimeState` (`Timing.ProcessInstanceData`). Host-skip + try/catch. | Engine — build + 2-instance |

### Modified files

| File | Change |
|------|--------|
| `src/Network/MessageLayer/MessageSerializer.cs` | Append `SetTimeState = 14` to `CampaignActionType` (`:548`). (Pure; test-linked — keep no game-type refs.) |
| `src/Network/CommandSync/CommandCodec.cs` | Add pure `SetTimePayload{bool Paused; int PresetIndex}` + `EncodeSetTime`/`DecodeSetTime`, and pure `TimeStatePayload{bool Paused; float Scale; long StartTimeTicks; long StartFixedTicks; long OwnNowTicks; long OwnFixedTicks}` + `EncodeTimeState`/`DecodeTimeState`. |
| `src/Network/CommandSync/InterceptRegistry.cs` | Add `SetTimeState` row (`RequiredPermission=ControlTime`, `SignatureConfirmed=true`). |
| `src/Network/CommandSync/PermissionGate.cs` | `RequiredPermission`: `case SetTimeState: return ControlTime;`. |
| `src/Validation/ActionValidator.cs` | `GetRequiredPermission`: `case SetTimeState: return ControlTime;`. |
| `src/Network/CommandSync/CommandExecutor.cs` | `case SetTimeState: ApplySetTime(action);` + `ApplySetTime` (via `TimeBridge`). |
| `src/Network/NetworkEngine.cs` | Add `BroadcastTimingState(TimeStatePayload)` (→ `0x34`, subtype `0x01`); implement `0x34` receive-case → decode subtype `0x01` → `ClientTimeMirror.Apply`; tick `TimeSyncBroadcaster` from `Update()` (host-only). |
| `Multiplayer.Tests/Multiplayer.Tests.csproj` | No new links needed — `CommandCodec.cs`, `PermissionGate.cs`, `InterceptRegistry.cs`, `MessageSerializer.cs` already linked. (Verify before assuming; if a future split moves a pure type out, link it.) |

**Build:** `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
**Tests:** `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
**In-game (integration, 2-instance):** per `multiplayer-second-instance-setup` — launch host + Goldberg-emu second instance; one hosts a geoscape campaign, the other joins with `ControlTime` granted; verify pause/resume/speed from EITHER peer mirrors on BOTH, the client clock follows the host with no independent hour-tick, and host auto-pause (aircraft arrival) pauses both.

---

## Task 1 — Enum + SetTime payload codec (pure, full TDD)

**Files:**
- Modify `src/Network/MessageLayer/MessageSerializer.cs` (enum, `:548`)
- Modify `src/Network/CommandSync/CommandCodec.cs` (add `SetTimePayload` + codec)
- Test: `Multiplayer.Tests/SetTimeCodecTests.cs` (Create)

**Steps:**
- [ ] Write failing test `SetTimeCodecTests.cs`:
  ```csharp
  using Multiplayer.Network.CommandSync;
  using Xunit;

  public class SetTimeCodecTests
  {
      [Fact]
      public void SetTimePayload_RoundTrips()
      {
          var src = new SetTimePayload { Paused = true, PresetIndex = 2 };
          var back = CommandCodec.DecodeSetTime(CommandCodec.EncodeSetTime(src));
          Assert.True(back.Paused);
          Assert.Equal(2, back.PresetIndex);
      }

      [Fact]
      public void SetTimePayload_Unpaused_ZeroIndex_RoundTrips()
      {
          var src = new SetTimePayload { Paused = false, PresetIndex = 0 };
          var back = CommandCodec.DecodeSetTime(CommandCodec.EncodeSetTime(src));
          Assert.False(back.Paused);
          Assert.Equal(0, back.PresetIndex);
      }
  }
  ```
- [ ] Run — expect FAIL (compile error: `SetTimePayload`/`EncodeSetTime` missing):
  `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release --filter SetTimeCodecTests`
- [ ] Add enum value — `MessageSerializer.cs`, change the last line of `CampaignActionType`:
  ```csharp
          StartTravel = 13,
          SetTimeState = 14
  ```
- [ ] Implement codec in `CommandCodec.cs` (append inside the `Multiplayer.Network.CommandSync` namespace):
  ```csharp
  public struct SetTimePayload
  {
      public bool Paused;
      public int PresetIndex;
  }
  ```
  and inside `public static class CommandCodec`:
  ```csharp
  public static byte[] EncodeSetTime(SetTimePayload p)
  {
      using (var ms = new MemoryStream())
      using (var bw = new BinaryWriter(ms))
      {
          bw.Write(p.Paused);
          bw.Write(p.PresetIndex);
          return ms.ToArray();
      }
  }

  public static SetTimePayload DecodeSetTime(byte[] data)
  {
      using (var ms = new MemoryStream(data))
      using (var br = new BinaryReader(ms))
      {
          return new SetTimePayload { Paused = br.ReadBoolean(), PresetIndex = br.ReadInt32() };
      }
  }
  ```
- [ ] Run — expect PASS: `dotnet test … --filter SetTimeCodecTests`
- [ ] Commit:
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): SetTimeState action type + SetTime payload codec"`

---

## Task 2 — TimeState (clock-mirror) payload codec (pure, full TDD)

**Files:**
- Modify `src/Network/CommandSync/CommandCodec.cs` (add `TimeStatePayload` + codec)
- Test: `Multiplayer.Tests/TimeStateCodecTests.cs` (Create)

**Steps:**
- [ ] Write failing test `TimeStateCodecTests.cs`:
  ```csharp
  using Multiplayer.Network.CommandSync;
  using Xunit;

  public class TimeStateCodecTests
  {
      [Fact]
      public void TimeStatePayload_RoundTrips()
      {
          var src = new TimeStatePayload
          {
              Paused = true,
              Scale = 1500f,
              StartTimeTicks = 123456789L,
              StartFixedTicks = 222L,
              OwnNowTicks = 987654321L,
              OwnFixedTicks = 333L
          };
          var back = CommandCodec.DecodeTimeState(CommandCodec.EncodeTimeState(src));
          Assert.True(back.Paused);
          Assert.Equal(1500f, back.Scale);
          Assert.Equal(123456789L, back.StartTimeTicks);
          Assert.Equal(222L, back.StartFixedTicks);
          Assert.Equal(987654321L, back.OwnNowTicks);
          Assert.Equal(333L, back.OwnFixedTicks);
      }
  }
  ```
- [ ] Run — expect FAIL (compile error): `dotnet test … --filter TimeStateCodecTests`
- [ ] Implement in `CommandCodec.cs`:
  ```csharp
  public struct TimeStatePayload
  {
      public bool Paused;
      public float Scale;
      public long StartTimeTicks;
      public long StartFixedTicks;
      public long OwnNowTicks;
      public long OwnFixedTicks;
  }
  ```
  and inside `CommandCodec`:
  ```csharp
  public static byte[] EncodeTimeState(TimeStatePayload p)
  {
      using (var ms = new MemoryStream())
      using (var bw = new BinaryWriter(ms))
      {
          bw.Write(p.Paused);
          bw.Write(p.Scale);
          bw.Write(p.StartTimeTicks);
          bw.Write(p.StartFixedTicks);
          bw.Write(p.OwnNowTicks);
          bw.Write(p.OwnFixedTicks);
          return ms.ToArray();
      }
  }

  public static TimeStatePayload DecodeTimeState(byte[] data)
  {
      using (var ms = new MemoryStream(data))
      using (var br = new BinaryReader(ms))
      {
          return new TimeStatePayload
          {
              Paused = br.ReadBoolean(),
              Scale = br.ReadSingle(),
              StartTimeTicks = br.ReadInt64(),
              StartFixedTicks = br.ReadInt64(),
              OwnNowTicks = br.ReadInt64(),
              OwnFixedTicks = br.ReadInt64()
          };
      }
  }
  ```
- [ ] Run — expect PASS: `dotnet test … --filter TimeStateCodecTests`
- [ ] Commit:
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): TimeState clock-mirror payload codec (TimeUnit ticks)"`

---

## Task 3 — Permission mapping: SetTimeState → ControlTime (pure, full TDD)

**Files:**
- Modify `src/Network/CommandSync/PermissionGate.cs` (`RequiredPermission` switch)
- Modify `src/Validation/ActionValidator.cs` (`GetRequiredPermission` switch — keep the two in sync)
- Test: `Multiplayer.Tests/PermissionGateTests.cs` (Modify — add cases)

**Steps:**
- [ ] Add failing tests to `PermissionGateTests.cs`:
  ```csharp
  [Fact]
  public void SetTimeState_RequiresControlTime()
  {
      Assert.Equal(CampaignPermission.ControlTime,
          PermissionGate.RequiredPermission(CampaignActionType.SetTimeState));
  }

  [Fact]
  public void IsAllowed_True_WhenGuidHasControlTime()
  {
      var g = System.Guid.NewGuid();
      PermissionManager.SetPermission(g, CampaignPermission.ControlTime, true);
      Assert.True(PermissionGate.IsAllowed(g, CampaignActionType.SetTimeState));
  }

  [Fact]
  public void IsAllowed_False_ForSetTimeState_WhenGuidLacksControlTime()
  {
      var g = System.Guid.NewGuid();
      PermissionManager.SetPermission(g, CampaignPermission.ManageResearch, true);
      Assert.False(PermissionGate.IsAllowed(g, CampaignActionType.SetTimeState));
  }
  ```
- [ ] Run — expect FAIL (`SetTimeState` falls to `default → FullCommander`): `dotnet test … --filter PermissionGateTests`
- [ ] Edit `PermissionGate.RequiredPermission` — add before `default:`:
  ```csharp
                  case CampaignActionType.SetTimeState:
                      return CampaignPermission.ControlTime;
  ```
- [ ] Edit `ActionValidator.GetRequiredPermission` — add the identical case (keep parity; this method also keys campaign validation):
  ```csharp
                  case CampaignActionType.SetTimeState:
                      return CampaignPermission.ControlTime;
  ```
- [ ] Run — expect PASS: `dotnet test … --filter PermissionGateTests`
- [ ] Commit:
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): gate SetTimeState on ControlTime in PermissionGate + ActionValidator"`

---

## Task 4 — InterceptRegistry row for SetTimeState (pure, full TDD)

**Files:**
- Modify `src/Network/CommandSync/InterceptRegistry.cs` (`_entries`)
- Test: `Multiplayer.Tests/InterceptRegistryTests.cs` (Modify — add case)

**Steps:**
- [ ] Add failing test to `InterceptRegistryTests.cs`:
  ```csharp
  [Fact]
  public void Lookup_SetTimeState_ReturnsConfirmedControlTimeEntry()
  {
      var e = InterceptRegistry.Lookup(CampaignActionType.SetTimeState);
      Assert.NotNull(e);
      Assert.Equal(CampaignPermission.ControlTime, e.RequiredPermission);
      Assert.True(e.SignatureConfirmed);
  }
  ```
- [ ] Run — expect FAIL (lookup returns null): `dotnet test … --filter InterceptRegistryTests`
- [ ] Add the row to `_entries` (after the `StartTravel` entry). The apply target is resolved inside `CommandExecutor` (via the live UI module), so the type/method names here are descriptive; `SignatureConfirmed=true` because the apply path is real:
  ```csharp
              [CampaignActionType.SetTimeState] = new InterceptEntry
              {
                  ActionType = CampaignActionType.SetTimeState,
                  RequiredPermission = CampaignPermission.ControlTime,
                  // Applied via the live UIModuleTimeControl (SelectTimePreset + OnPauseTime),
                  // resolved in CommandExecutor.ApplySetTime — not by the registry resolver.
                  DeclaringTypeName = "PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl",
                  MethodName = "SetTimeState",
                  ParamTypeNames = null,
                  SignatureConfirmed = true
              },
  ```
- [ ] Run — expect PASS: `dotnet test … --filter InterceptRegistryTests`
- [ ] Commit:
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): register SetTimeState intercept entry (ControlTime, confirmed)"`

---

## Task 5 — TimeBridge: Unity-side accessors (build-verified; integration only)

**Files:**
- Create `src/Network/CommandSync/TimeBridge.cs`

> **No unit test** — `TimeBridge` reflects into live game types (`UIModuleTimeControl`, `GeoscapeView`, `Timing`) and cannot link without the game DLLs (kept out of the Unity-free test set, exactly like `GeoBridge`). Verification = **compiles** + the Task-9 in-game 2-instance run exercises every method.

**Steps:**
- [ ] Create `src/Network/CommandSync/TimeBridge.cs`:
  ```csharp
  using System;
  using HarmonyLib;
  using Multiplayer.Network.CommandSync;

  namespace Multiplayer.Network.CommandSync
  {
      // Unity-side id<->engine bridge for time sync. AccessTools/Traverse only — the mod never
      // hard-references game types (matches GeoBridge). Reaches the live UIModuleTimeControl via
      // the active GeoscapeView, and the clock via GeoLevelController.Timing.
      internal static class TimeBridge
      {
          // Active GeoscapeView.GeoscapeModules.TimeControlModule, or null (not on geoscape).
          public static object GetTimeControlModule()
          {
              var view = GetGeoscapeView();
              if (view == null) return null;
              var modules = AccessTools.Field(view.GetType(), "GeoscapeModules")?.GetValue(view);
              if (modules == null) return null;
              return AccessTools.Field(modules.GetType(), "TimeControlModule")?.GetValue(modules);
          }

          // GameUtl.CurrentLevel().GetComponent<GeoLevelController>().View (GeoscapeView), or null.
          public static object GetGeoscapeView()
          {
              var geoLevel = GeoBridge.GetGeoLevelController();
              if (geoLevel == null) return null;
              return AccessTools.Property(geoLevel.GetType(), "View")?.GetValue(geoLevel);
          }

          // GeoLevelController.Timing (the authoritative clock), or null.
          public static object GetTiming()
          {
              var geoLevel = GeoBridge.GetGeoLevelController();
              if (geoLevel == null) return null;
              return AccessTools.Property(geoLevel.GetType(), "Timing")?.GetValue(geoLevel);
          }

          // Current SelectedPresetTime (int) off the live module; -1 if unavailable.
          public static int GetCurrentPresetIndex(object timeModule)
          {
              if (timeModule == null) return -1;
              var v = AccessTools.Field(timeModule.GetType(), "SelectedPresetTime")?.GetValue(timeModule);
              return v is int i ? i : -1;
          }

          // Current paused state off the module's private _timing; false if unavailable.
          public static bool GetCurrentPaused(object timeModule)
          {
              if (timeModule == null) return false;
              var timing = AccessTools.Field(timeModule.GetType(), "_timing")?.GetValue(timeModule);
              if (timing == null) return false;
              var v = AccessTools.Property(timing.GetType(), "Paused")?.GetValue(timing);
              return v is bool b && b;
          }

          // Authoritative apply on host/clients: drive the live module so SelectedPresetTime, animator,
          // and Timing.Scale/Paused stay coherent. Runs under CommandRelay.IsApplying (set by ApplyResult)
          // so the intercept prefixes treat the nested calls as re-entrant and let them through.
          // Fallback: poke Timing directly if the module is unreachable.
          public static void ApplySetTime(SetTimePayload p)
          {
              var module = GetTimeControlModule();
              if (module != null)
              {
                  // SelectTimePreset(int) clamps internally; OnPauseTime(bool) is private.
                  AccessTools.Method(module.GetType(), "SelectTimePreset", new[] { typeof(int) })
                             ?.Invoke(module, new object[] { p.PresetIndex });
                  AccessTools.Method(module.GetType(), "OnPauseTime", new[] { typeof(bool) })
                             ?.Invoke(module, new object[] { p.Paused });
                  return;
              }
              // Fallback: no module (e.g. timing change before UI ready) -> poke the clock's Paused only.
              var timing = GetTiming();
              if (timing != null)
                  AccessTools.Property(timing.GetType(), "Paused")?.SetValue(timing, p.Paused);
          }

          // Host: snapshot the clock for the periodic mirror. Returns null if no clock.
          public static SetTimeStateSnapshot RecordHostState()
          {
              var timing = GetTiming();
              if (timing == null) return null;
              var data = AccessTools.Method(timing.GetType(), "RecordInstanceData")?.Invoke(timing, null);
              if (data == null) return null;
              return new SetTimeStateSnapshot
              {
                  Payload = new TimeStatePayload
                  {
                      Paused = (bool)AccessTools.Field(data.GetType(), "Paused").GetValue(data),
                      Scale = (float)AccessTools.Field(data.GetType(), "Scale").GetValue(data),
                      StartTimeTicks = TicksOf(AccessTools.Field(data.GetType(), "StartTime").GetValue(data)),
                      StartFixedTicks = TicksOf(AccessTools.Field(data.GetType(), "StartFixedTime").GetValue(data)),
                      OwnNowTicks = TicksOf(AccessTools.Field(data.GetType(), "OwnNow").GetValue(data)),
                      OwnFixedTicks = TicksOf(AccessTools.Field(data.GetType(), "OwnFixedNow").GetValue(data))
                  }
              };
          }

          // Client: force the clock to the host snapshot via Timing.ProcessInstanceData (sets fields
          // directly, fires no events -> no re-intercept, bypasses the SetGamePauseState TimeLimit guard).
          public static void ApplyTimeState(TimeStatePayload p)
          {
              var timing = GetTiming();
              if (timing == null) return;
              var tidType = AccessTools.TypeByName("Base.Core.TimingInstanceData");
              if (tidType == null) return;
              var data = Activator.CreateInstance(tidType);
              AccessTools.Field(tidType, "Paused").SetValue(data, p.Paused);
              AccessTools.Field(tidType, "Scale").SetValue(data, p.Scale);
              AccessTools.Field(tidType, "StartTime").SetValue(data, TimeUnitFromTicks(p.StartTimeTicks));
              AccessTools.Field(tidType, "StartFixedTime").SetValue(data, TimeUnitFromTicks(p.StartFixedTicks));
              AccessTools.Field(tidType, "OwnNow").SetValue(data, TimeUnitFromTicks(p.OwnNowTicks));
              AccessTools.Field(tidType, "OwnFixedNow").SetValue(data, TimeUnitFromTicks(p.OwnFixedTicks));
              AccessTools.Method(timing.GetType(), "ProcessInstanceData", new[] { tidType })
                         ?.Invoke(timing, new[] { data });
          }

          // TimeUnit -> ticks via the public TimeSpan getter (no private _time reflection).
          private static long TicksOf(object timeUnit)
          {
              if (timeUnit == null) return 0L;
              var ts = AccessTools.Property(timeUnit.GetType(), "TimeSpan")?.GetValue(timeUnit);
              return ts is TimeSpan t ? t.Ticks : 0L;
          }

          // ticks -> TimeUnit via public static TimeUnit.FromTimeSpan(TimeSpan).
          private static object TimeUnitFromTicks(long ticks)
          {
              var tuType = AccessTools.TypeByName("Base.Core.TimeUnit");
              if (tuType == null) return null;
              var from = AccessTools.Method(tuType, "FromTimeSpan", new[] { typeof(TimeSpan) });
              return from?.Invoke(null, new object[] { TimeSpan.FromTicks(ticks) });
          }
      }

      // Non-null wrapper so callers distinguish "no clock" (null) from a valid snapshot.
      internal sealed class SetTimeStateSnapshot
      {
          public TimeStatePayload Payload;
      }
  }
  ```
  > NOTE — verify against the live DLL at implementation time (R8): `GeoLevelController.View` (the `GeoscapeView` accessor used here) — doc-12 cites `GeoscapeView` reached via the level; confirm the property name `View` on `GeoLevelController` (or substitute the real accessor) before relying on `GetGeoscapeView`. If `View` is absent, fall back to `UnityEngine.Object.FindObjectOfType(UIModuleTimeControl)` in `GetTimeControlModule`. (Build will compile either way — it's reflection — so this is an in-game check.)
- [ ] Build — expect SUCCESS: `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
- [ ] Commit:
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): TimeBridge reflection accessors (module + clock record/apply)"`

---

## Task 6 — CommandExecutor.ApplySetTime + apply branch (build-verified; integration only)

**Files:**
- Modify `src/Network/CommandSync/CommandExecutor.cs`

> **No unit test** — `CommandExecutor` already touches `GeoBridge`/`AccessTools` and is not in the Unity-free test set. The pure decode it relies on (`CommandCodec.DecodeSetTime`) is covered by Task 1. Verification = compiles + Task-9 in-game.

**Steps:**
- [ ] Add the apply branch to the `switch` in `Execute`:
  ```csharp
                  case CampaignActionType.SetTimeState:
                      ApplySetTime(action);
                      break;
  ```
- [ ] Add the method (after `ApplyStartTravel`):
  ```csharp
          // Apply an authorized time change on host + clients. Decodes {Paused, PresetIndex} and drives
          // the live UIModuleTimeControl via TimeBridge (coherent Scale/animator/pause). Runs under
          // CommandRelay.IsApplying (set by ApplyResult) so the OnPauseTime/SelectTimePreset prefixes
          // see a re-entrant apply and execute the real writes instead of re-sending.
          private static void ApplySetTime(CampaignActionMessage action)
          {
              var p = CommandCodec.DecodeSetTime(action.Payload);
              TimeBridge.ApplySetTime(p);
          }
  ```
- [ ] Build — expect SUCCESS: `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
- [ ] Run full suite — expect PASS (no regression): `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
- [ ] Commit:
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): CommandExecutor.ApplySetTime drives live time module under IsApplying"`

---

## Task 7 — Client intercept prefixes: pause + speed (build-verified; integration only)

**Files:**
- Create `src/Harmony/TimePauseInterceptPatch.cs`
- Create `src/Harmony/TimeSpeedInterceptPatch.cs`

> **No unit test** — Harmony patches against live game types. Verification = compiles (`Prepare` resolves) + Task-9 in-game (client pause/speed mirrors to host, no local-only change).

**Steps:**
- [ ] Create `src/Harmony/TimePauseInterceptPatch.cs`:
  ```csharp
  using System;
  using System.Reflection;
  using HarmonyLib;
  using Multiplayer.Network;
  using Multiplayer.Network.CommandSync;
  using Multiplayer.Network.MessageLayer;

  namespace Multiplayer.Harmony
  {
      // Client intercept of the pause funnel UIModuleTimeControl.OnPauseTime(bool). Client -> encode a
      // SetTimeState{Paused=arg, PresetIndex=current} + relay to host + block local write (return false).
      // Host / re-entrant apply -> return true (execute the real write). Mirrors StartTravelInterceptPatch.
      [HarmonyPatch]
      public static class TimePauseInterceptPatch
      {
          private static MethodBase _target;

          public static bool Prepare()
          {
              var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
              if (t == null) return false;
              _target = AccessTools.Method(t, "OnPauseTime", new[] { typeof(bool) });
              return _target != null;
          }

          public static MethodBase TargetMethod() => _target;

          public static bool Prefix(object __instance, bool pause)
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive) return true;  // single player
              if (CommandRelay.IsApplying) return true;              // re-entrant apply: execute
              if (engine.IsHost) return true;                        // host-origin: postfix-free path; host clock is authoritative

              var msg = new CampaignActionMessage
              {
                  ActionId = Guid.NewGuid(),
                  ActionType = CampaignActionType.SetTimeState,
                  TargetId = "",
                  Payload = CommandCodec.EncodeSetTime(new SetTimePayload
                  {
                      Paused = pause,
                      PresetIndex = TimeBridge.GetCurrentPresetIndex(__instance)
                  }),
                  Timestamp = DateTime.UtcNow.Ticks
              };
              CommandRelay.Instance?.RelayFromClient(msg);
              return false;  // block the client's local _timing.Paused write
          }
      }
  }
  ```
- [ ] Create `src/Harmony/TimeSpeedInterceptPatch.cs`:
  ```csharp
  using System;
  using System.Reflection;
  using HarmonyLib;
  using Multiplayer.Network;
  using Multiplayer.Network.CommandSync;
  using Multiplayer.Network.MessageLayer;

  namespace Multiplayer.Harmony
  {
      // Client intercept of the speed funnel UIModuleTimeControl.SelectTimePreset(int). Client -> encode a
      // SetTimeState{Paused=current, PresetIndex=arg} + relay + block local write. Host re-applies via the
      // same method (which clamps the index), so we send the raw requested index. Mirrors the pause patch.
      [HarmonyPatch]
      public static class TimeSpeedInterceptPatch
      {
          private static MethodBase _target;

          public static bool Prepare()
          {
              var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
              if (t == null) return false;
              _target = AccessTools.Method(t, "SelectTimePreset", new[] { typeof(int) });
              return _target != null;
          }

          public static MethodBase TargetMethod() => _target;

          public static bool Prefix(object __instance, int presetIndex)
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive) return true;
              if (CommandRelay.IsApplying) return true;
              if (engine.IsHost) return true;

              var msg = new CampaignActionMessage
              {
                  ActionId = Guid.NewGuid(),
                  ActionType = CampaignActionType.SetTimeState,
                  TargetId = "",
                  Payload = CommandCodec.EncodeSetTime(new SetTimePayload
                  {
                      Paused = TimeBridge.GetCurrentPaused(__instance),
                      PresetIndex = presetIndex
                  }),
                  Timestamp = DateTime.UtcNow.Ticks
              };
              CommandRelay.Instance?.RelayFromClient(msg);
              return false;  // block the client's local SelectedPresetTime / Scale write
          }
      }
  }
  ```
  > Host-origin note: unlike `StartTravel`, the host time path needs **no postfix broadcast of the discrete action** — the host's authoritative clock change is propagated by the continuous `TimeSyncBroadcaster` (Task 8) which catches the host's own `SetGamePauseState`/scale change and pushes `0x34`. So the host simply executes its own UI write (`return true`) and the mirror fans out. (Keeping no postfix avoids a redundant 0x31 round-trip for time.)
- [ ] Build — expect SUCCESS: `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
- [ ] Commit:
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): client intercept prefixes for pause + speed -> SetTimeState to host"`

---

## Task 8 — Clock mirror over 0x34: broadcaster + client apply + engine wiring (build-verified; integration only)

**Files:**
- Create `src/Network/CommandSync/TimeSyncBroadcaster.cs`
- Create `src/Network/CommandSync/ClientTimeMirror.cs`
- Modify `src/Network/NetworkEngine.cs` (add `BroadcastTimingState`, `0x34` receive-case, tick broadcaster in `Update()`)

> **No unit test** — all three touch `NetworkEngine`/game types. The wire codec (`Encode/DecodeTimeState`) is covered by Task 2. Verification = compiles + Task-9 in-game (client clock follows host; drift corrected within ~0.5s).

**Steps:**
- [ ] Create `src/Network/CommandSync/TimeSyncBroadcaster.cs`:
  ```csharp
  using UnityEngine;

  namespace Multiplayer.Network.CommandSync
  {
      // Host-only continuous clock mirror. Ticked from NetworkEngine.Update() every frame; throttles a
      // 0x34 TimingState broadcast to ~0.5s of real time, plus BroadcastNow() for immediate push after a
      // time change / auto-pause. Reads the host clock via TimeBridge.RecordHostState.
      public static class TimeSyncBroadcaster
      {
          private const float IntervalSeconds = 0.5f;
          private static float _accum;

          // Call once per frame from the host's NetworkEngine.Update().
          public static void Tick(NetworkEngine engine, float deltaTime)
          {
              if (engine == null || !engine.IsActive || !engine.IsHost) return;
              _accum += deltaTime;
              if (_accum < IntervalSeconds) return;
              _accum = 0f;
              BroadcastNow();
          }

          // Immediate push of the current host clock state to all peers (no-op off-host / no clock).
          public static void BroadcastNow()
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive || !engine.IsHost) return;
              var snap = TimeBridge.RecordHostState();
              if (snap == null) return;
              engine.BroadcastTimingState(snap.Payload);
          }
      }
  }
  ```
- [ ] Create `src/Network/CommandSync/ClientTimeMirror.cs`:
  ```csharp
  using UnityEngine;

  namespace Multiplayer.Network.CommandSync
  {
      // Client-only apply of a host clock snapshot (0x34, subtype TimingState). Forces the local clock to
      // host via Timing.ProcessInstanceData (TimeBridge.ApplyTimeState) -> displayed Now/Scale/Paused match
      // host, frame-drift corrected. Host ignores (it owns the clock). ProcessInstanceData fires no events
      // and reschedules nothing, so no re-intercept and no SetGamePauseState TimeLimit guard.
      public static class ClientTimeMirror
      {
          public static void Apply(TimeStatePayload payload)
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive || engine.IsHost) return;
              try { TimeBridge.ApplyTimeState(payload); }
              catch (System.Exception ex) { Debug.LogError($"[Multiplayer] ClientTimeMirror apply failed: {ex}"); }
          }
      }
  }
  ```
- [ ] In `NetworkEngine.cs`, add the typed timing broadcast (subtype-discriminated `0x34`) near `BroadcastCampaignState` (`:295`):
  ```csharp
          // Host -> all: authoritative geoscape clock snapshot. 0x34 payload = [subtype:byte][body];
          // subtype 0x01 = TimingState (Increment-1). Future increments add 0x02 = WorldDelta.
          public void BroadcastTimingState(Multiplayer.Network.CommandSync.TimeStatePayload payload)
          {
              var body = Multiplayer.Network.CommandSync.CommandCodec.EncodeTimeState(payload);
              var buf = new byte[body.Length + 1];
              buf[0] = 0x01; // TimingState subtype
              System.Array.Copy(body, 0, buf, 1, body.Length);
              var msg = new NetworkMessage(PacketType.CampaignStateUpdate, buf);
              BroadcastToAll(msg);
          }
  ```
- [ ] Implement the `0x34` receive-case in `RouteMessage` (replace the TODO at `:534-536`):
  ```csharp
                  case PacketType.CampaignStateUpdate:
                      if (msg.Payload != null && msg.Payload.Length >= 1 && msg.Payload[0] == 0x01)
                      {
                          var body = new byte[msg.Payload.Length - 1];
                          System.Array.Copy(msg.Payload, 1, body, 0, body.Length);
                          var ts = Multiplayer.Network.CommandSync.CommandCodec.DecodeTimeState(body);
                          Multiplayer.Network.CommandSync.ClientTimeMirror.Apply(ts);
                      }
                      break;
  ```
- [ ] Tick the broadcaster from `Update()` (host cadence). Replace `:304-308`:
  ```csharp
          public void Update()
          {
              Transport?.Update();
              Session?.Update();
              SaveTransfer?.Update();
              Multiplayer.Network.CommandSync.TimeSyncBroadcaster.Tick(this, Time.deltaTime);
          }
  ```
  (`Time` is `UnityEngine.Time`; `NetworkEngine.cs` already `using UnityEngine;` — confirm the using at the top; add if missing.)
- [ ] Build — expect SUCCESS: `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
- [ ] Run full suite — expect PASS: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
- [ ] Commit:
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): host clock-mirror broadcaster + client 0x34 TimingState apply"`

---

## Task 9 — Client sim freeze (R1) + auto-pause propagation (build-verified; integration only)

**Files:**
- Create `src/Harmony/ClientHourlySimSuppressPatch.cs`
- Create `src/Harmony/AutoPauseSyncPatch.cs`

> **No unit test** — Harmony patches against live game types. Verification = compiles (`Prepare` resolves) + the **in-game 2-instance** checks below.

**Steps:**
- [ ] Create `src/Harmony/ClientHourlySimSuppressPatch.cs`:
  ```csharp
  using System;
  using System.Reflection;
  using HarmonyLib;
  using Multiplayer.Network;

  namespace Multiplayer.Harmony
  {
      // R1 fix: on the CLIENT, skip GeoLevelController.LevelHourlyUpdateCrt's heavy authoritative body
      // (income/research/manufacture/recruits/faction AI/interception) so it never double-simulates. The
      // coroutine MUST keep living, so we set __result to the same reschedule the real method returns
      // (NextUpdate.After(TimeUtils.GetNextHour(timing))) -> the displayed clock still advances; the host
      // is the only simulator. Host / single-player -> run the real body.
      [HarmonyPatch]
      public static class ClientHourlySimSuppressPatch
      {
          private static MethodBase _target;
          private static MethodInfo _getNextHour;   // TimeUtils.GetNextHour(Timing) -> TimeUnit
          private static MethodInfo _after;         // NextUpdate.After(TimeUnit) -> NextUpdate

          public static bool Prepare()
          {
              var glc = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
              var timing = AccessTools.TypeByName("Base.Core.Timing");
              if (glc == null || timing == null) return false;
              _target = AccessTools.Method(glc, "LevelHourlyUpdateCrt", new[] { timing });

              var timeUtils = AccessTools.TypeByName("TimeUtils");
              _getNextHour = timeUtils == null ? null : AccessTools.Method(timeUtils, "GetNextHour", new[] { timing });

              var nextUpdate = AccessTools.TypeByName("Base.Core.NextUpdate");
              var timeUnit = AccessTools.TypeByName("Base.Core.TimeUnit");
              _after = (nextUpdate == null || timeUnit == null)
                  ? null : AccessTools.Method(nextUpdate, "After", new[] { timeUnit });

              return _target != null && _getNextHour != null && _after != null;
          }

          public static MethodBase TargetMethod() => _target;

          // __result is NextUpdate (struct). Skip body on client; keep the coroutine rescheduling.
          public static bool Prefix(object timing, ref object __result)
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive) return true;  // single player: real sim
              if (engine.IsHost) return true;                        // host: real sim (authoritative)

              var nextHour = _getNextHour.Invoke(null, new[] { timing });   // TimeUnit
              __result = _after.Invoke(null, new[] { nextHour });           // NextUpdate.After(nextHour)
              return false;  // skip the heavy authoritative body on the client
          }
      }
  }
  ```
  > Boxing note: `__result` is the `NextUpdate` struct boxed as `object` (the patched method's return type is `NextUpdate`). Harmony unboxes `ref object __result` back to the struct return — the standard pattern for a non-primitive struct return. Confirm in-game that the client's clock display advances and no hour-tick side effects (resource income, research progress) occur on the client.
- [ ] Create `src/Harmony/AutoPauseSyncPatch.cs`:
  ```csharp
  using System.Reflection;
  using HarmonyLib;
  using Multiplayer.Network;
  using Multiplayer.Network.CommandSync;

  namespace Multiplayer.Harmony
  {
      // Auto-pause sync at the single funnel GeoscapeView.SetGamePauseState(bool) — every auto-pause
      // (vehicle arrival, event popup, mission launch via RequestGamePause->RequestPauseCrt) flips
      // Timing.Paused here. HOST: postfix -> push the new clock state immediately (TimeSyncBroadcaster).
      // CLIENT: prefix -> return false (suppress independent local auto-pause; the host mirror drives the
      // client clock, preventing desync). Re-entrant host applies are fine (postfix just re-pushes state).
      [HarmonyPatch]
      public static class AutoPauseSyncPatch
      {
          private static MethodBase _target;

          public static bool Prepare()
          {
              var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
              if (t == null) return false;
              _target = AccessTools.Method(t, "SetGamePauseState", new[] { typeof(bool) });
              return _target != null;
          }

          public static MethodBase TargetMethod() => _target;

          // Client: block local auto-pause. Host / single player: let it run.
          public static bool Prefix()
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive) return true;  // single player
              if (engine.IsHost) return true;                        // host: authoritative
              return false;                                          // client: suppress local auto-pause
          }

          // Host: after the clock flips, push it to clients right away.
          public static void Postfix()
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive || !engine.IsHost) return;
              TimeSyncBroadcaster.BroadcastNow();
          }
      }
  }
  ```
  > Verify (R5/R8) in-game: the host's `SetGamePauseState` TimeLimit guard still works host-side (unaffected — we only postfix). The client never calls the guard (prefix-blocked) and is driven by `ProcessInstanceData`, which bypasses it — confirm the client does not get stuck paused at `TimeLimit` while host runs.
- [ ] Build — expect SUCCESS: `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
- [ ] Run full suite — expect PASS (no regression): `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
- [ ] **In-game 2-instance verification** (replaces unit tests for this task; per `multiplayer-second-instance-setup`):
  - [ ] Host a campaign; client joins with `ControlTime` granted. Client presses pause → BOTH pause; client increases speed → BOTH change speed; host pause/speed → BOTH mirror. (D1/D2/D3 + Task 7.)
  - [ ] Let time run several in-game hours: confirm the **client clock advances** (display) but client-side resource income / research progress do NOT tick independently (only host's do). (R1 / D4 — `ClientHourlySimSuppressPatch`.)
  - [ ] Send a host aircraft to a site; on arrival the host auto-pauses → the **client auto-pauses too**; the client does NOT independently auto-pause on its own UI events. (D6 — `AutoPauseSyncPatch`.)
  - [ ] Force a brief FPS gap on the client; confirm the clock snaps back to host within ~0.5s (the periodic `0x34` mirror corrects drift). (R2.)
  - [ ] Revoke `ControlTime` from the client; client pause/speed inputs are rejected (no effect), host-only time control. (Permission gate.)
- [ ] Commit (after in-game pass):
  `git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(time-sync): freeze client hourly sim (R1) + host auto-pause propagation"`

---

## Future plans (out of scope — do NOT implement here)

- **Increment 2 — world-state delta broadcast.** Host broadcasts geoscape state deltas (resource/research/manufacture/recruit/faction results produced by its authoritative `LevelHourlyUpdateCrt`) so the client's frozen sim sees the outcomes. Reuse `0x34` with subtype `0x02 = WorldDelta`. The `ClientHourlySimSuppressPatch` already guarantees the client produces none of these locally, so the host's deltas are the sole source.
- **Increment 3 — route base/research/manufacture/squad inputs** through the same `CampaignActionType` intercept path (the curated registry rows already exist, pending signatures).
- **View/menu-pause separation (doc-12 R4).** Re-enable purely-local menu pauses on the client without desyncing the authoritative clock (e.g. a separate cosmetic display-pause vs the synced geoscape clock).

---

## Self-Review

### Spec coverage vs prompt + doc-12

| Requirement | Covered by | Status |
|-------------|-----------|--------|
| New `CampaignActionType` for time (= 14) + exact payload encoding | Task 1 (`SetTimeState=14`, `SetTimePayload{bool Paused; int PresetIndex}`, `bw.Write(bool)+bw.Write(int)`) | ✅ |
| Client intercept on pause + speed UI → `CampaignActionRequest 0x30`, suppress local | Task 7 (prefix `OnPauseTime` + `SelectTimePreset`, `RelayFromClient` + `return false`) | ✅ |
| `InterceptRegistry` registration | Task 4 (`SetTimeState` row, ControlTime, confirmed) | ✅ |
| `PermissionGate`/`ActionValidator` → `ControlTime` | Task 3 (both switches) | ✅ |
| `HostArbiter` handles it (approve → apply + broadcast) | Reused unchanged (grounding note) — flows through once registered + executor branch | ✅ |
| `CommandExecutor` apply on BOTH peers under `IsApplying` | Task 6 (`ApplySetTime` → `TimeBridge.ApplySetTime`, runs inside `ApplyResult`'s guard) | ✅ |
| Client uses `ProcessInstanceData` for authoritative mirror | Task 8 (`ClientTimeMirror`/`TimeBridge.ApplyTimeState`) | ✅ |
| Client local-sim freeze (R1) — exact seam | Task 9 (`ClientHourlySimSuppressPatch` on `GeoLevelController.cs:777 LevelHourlyUpdateCrt`, skip body keep reschedule) | ✅ |
| `0x34 CampaignStateUpdate` periodic `TimingInstanceData` mirror + missing receive-case | Task 8 (`BroadcastTimingState` subtype `0x01`, receive-case implemented) | ✅ |
| Host auto-pause propagation; client must not independently auto-pause | Task 9 (`AutoPauseSyncPatch` postfix host / prefix-block client on `SetGamePauseState`) | ✅ |
| R2 frame-coupled drift | Task 8 periodic ~0.5s `0x34` re-sync | ✅ addressed |
| R3 RNG / event determinism | Client runs NO hourly sim (Task 9) → rolls no events/recruits/loot locally; host-only. Event *generation* host-gating is Stage-3 scope | ✅ addressed (events deferred to Inc 2/3, but client can't roll them now) |
| R5 TimeLimit unpause guard | Client applies via `ProcessInstanceData` (bypasses `SetGamePauseState` guard); host keeps its guard. Documented in Task 8/9 | ✅ addressed |
| R6 parent-chain pause | `RecordInstanceData` records `_paused` (own), `ProcessInstanceData` sets own `_paused`; the periodic mirror reflects host effective state. Noted | ✅ noted |
| R7 ×300 vs raw Scale | Payload uses `PresetIndex`, never raw Scale/×300 | ✅ |
| R8 verify vs DLL | Grounding table verified vs decompile; two explicit in-game re-verify flags (`GeoLevelController.View` accessor in Task 5; `NextUpdate` boxing in Task 9) | ✅ flagged |

### Open risks deferred (with reason)

- **R4 (view/menu-pause separation):** Deferred to a future increment. In Increment-1 the client clock is fully host-driven, so a client opening a local menu will NOT pause its (mirrored) clock — acceptable trade-off to keep the authoritative clock coherent; full cosmetic-vs-authoritative pause split is non-trivial and out of scope. Documented in Future Plans.
- **Increment-2 world-state deltas:** The client's frozen hourly sim means it won't locally see income/research outcomes until Increment-2 broadcasts them. Acceptable for an isolated time-sync increment; called out as the immediate next increment.
- **Host-origin discrete time action has no 0x31 postfix** (unlike StartTravel): intentional — the continuous `0x34` mirror already propagates host clock changes; a discrete 0x31 for host-origin time would be redundant. Client-origin still uses 0x30→apply→(host clock changes)→0x34 mirror. Documented in Task 7.

### Placeholder scan
No `TODO`/`TBD`/`similar to Task N`/`add error handling` placeholders — every step shows real symbol names + real code. The only forward references are the explicit, justified in-game R8 re-verifications (Task 5 `GeoLevelController.View`; Task 9 `NextUpdate` struct boxing), which are integration checks by design, not omissions.

### Type / signature consistency across tasks
- `SetTimePayload{bool Paused; int PresetIndex}` — defined Task 1; produced in Task 7 prefixes; consumed in Task 6 `ApplySetTime`/`TimeBridge.ApplySetTime`. Field names consistent.
- `TimeStatePayload{bool Paused; float Scale; long StartTimeTicks/StartFixedTicks/OwnNowTicks/OwnFixedTicks}` — defined Task 2; produced in Task 5 `TimeBridge.RecordHostState`; serialized in Task 8 `BroadcastTimingState`; deserialized in Task 8 receive-case; consumed in Task 5/8 `ApplyTimeState`. 6 fields consistent end-to-end; map 1:1 to `TimingInstanceData{Paused,Scale,StartTime,StartFixedTime,OwnNow,OwnFixedNow}` via `TimeUnit.TimeSpan.Ticks` ⇄ `TimeUnit.FromTimeSpan(TimeSpan.FromTicks)`.
- `CampaignActionType.SetTimeState = 14` — added Task 1; referenced in Tasks 3,4,6,7. Single value (D1), no `SetTimePaused`/`SetTimeScale` split.
- `CommandExecutor.Execute` switch (Task 6) ↔ `InterceptRegistry` row (Task 4) ↔ `PermissionGate`/`ActionValidator` (Task 3): all three keyed by `SetTimeState`; `ApplyResult` skips unconfirmed rows, so `SignatureConfirmed=true` (Task 4) is REQUIRED for the executor branch to run — consistent.
- `TimeSyncBroadcaster.BroadcastNow()` (Task 8) called from `AutoPauseSyncPatch.Postfix` (Task 9) and `Tick` (Task 8) — same static signature.
- `NetworkEngine.BroadcastTimingState(TimeStatePayload)` (Task 8) ↔ `ClientTimeMirror.Apply(TimeStatePayload)` (Task 8) — receive-case decodes then calls `Apply`. Consistent.
