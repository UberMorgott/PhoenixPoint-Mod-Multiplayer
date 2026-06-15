# Co-op Action-Sync & Permission Engine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The repo uses **runtime reflection** for all game types (game DLLs are not compile-time references) — bind game methods via `AccessTools.TypeByName` / `AccessTools.Method` exactly like `src/Harmony/TimeControlPatches.cs` and `src/Network/TimeSync/TimeSyncManager.cs` already do. Verify every game signature with Serena against `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp` before binding.

**Goal:** Build a generic, permission-gated engine that synchronizes all in-game currency (vanilla + mod), research, manufacturing, base construction/repair, and geoscape event choices across host + clients, with last-writer-wins conflict resolution and built-in (currently open) access control.

**Architecture:** Host-authoritative *discrete-action relay* + host-authoritative *currency echo*, layered on the existing `NetworkEngine` exactly like the `TimeSyncManager` subsystem. We deliberately do NOT continuously stream geoscape state (failed approach SD-AIDR) nor hand-roll per-action wire code with no shared infra (failed approach command-relay v1). Instead: (1) currency converges through one host→client snapshot echo driven by `Wallet.ResourcesChanged`, covering all ~60 mutation call-sites + mod currency for free; (2) discrete player commands flow through one generic `ISyncedAction` bus (register + intercept), host sequences them (last-writer-wins), clients reproduce the same discrete mutation under a re-entrancy guard; (3) timed-op progress is never streamed — only `start` and host-driven `completion` are synced as discrete actions.

**Tech Stack:** C# (.NET Framework, PhoenixPoint mod), HarmonyX (`Harmony` / `AccessTools`), existing `NetworkMessage` + `MessageSerializer` (BinaryWriter/Reader) wire layer, xUnit (`Multipleer.Tests`).

---

## Background facts (verified via Serena, 2026-06-15)

**Network layer**
- Role: `NetworkEngine.IsHost` (`src/Network/NetworkEngine.cs:16`). Singleton: `NetworkEngine.Instance`.
- Send: `BroadcastToAll(NetworkMessage)`, `BroadcastExcept(ulong, NetworkMessage)`, `SendToClient(ulong, NetworkMessage)`, `SendToHost(NetworkMessage)`.
- Receive: `OnPacketReceived` → `RouteMessage(NetworkMessage)` — a `switch (msg.Type)` over `PacketType` (`src/Network/NetworkEngine.cs:334`). No handler registry; add a `case`.
- Subsystem lifecycle template = `TimeSyncManager`: created in `NetworkEngine.Initialize()` (`:54` `TimeSync = new TimeSyncManager(this)`), ticked in `NetworkEngine.Update()` (`:265` `TimeSync?.Tick()`).
- Envelope: `NetworkMessage` (`src/Network/MessageLayer/NetworkMessage.cs`) — `new NetworkMessage(PacketType, byte[] payload)`. Payload POCO codecs in `MessageSerializer` (`src/Network/MessageLayer/MessageSerializer.cs`) using `BinaryWriter`/`BinaryReader` over `MemoryStream`.
- `PacketType` enum (`src/Network/MessageLayer/PacketType.cs`): used groups Connection 0x01-0x09, Session 0x10-0x1F, Tactical 0x20-0x27, Campaign 0x30-0x3A, Management 0x40-0x43, Chat 0x50, Transport 0xF0. **Free for us: new ActionSync group 0x60-0x6F.**

**Permission layer (exists, keep + extend)**
- `CampaignPermission` `[Flags]` (`src/Validation/PermissionManager.cs:6-20`): `None=0, ControlSoldiers=1<<0, ManageEquipment=1<<1, ManageBases=1<<2, ManageResearch=1<<3, ManageManufacturing=1<<4, ManageRecruitment=1<<5, ManageAircraft=1<<6, ControlTime=1<<7, ForceEndTurn=1<<8, FullCommander=1<<9`.
- `PermissionManager` static (`src/Validation/PermissionManager.cs:22`): `HasCampaignPermission(Guid, CampaignPermission) -> bool` (FullCommander bypasses), `SetPermission`, `GetPermissions`, roster helpers.
- Default: every joiner gets `FullCommander` (`SessionManager.HandleConnectionRequest:219`). Interim "everyone can do everything" — by design.
- Local identity: `ClientIdentity.PlayerGuid` (`src/Network/ClientIdentity.cs`). Example gate already in use: `TimeSyncManager.cs:675` `PermissionManager.HasCampaignPermission(ClientIdentity.PlayerGuid, CampaignPermission.ControlTime)`.
- Roster: `NetworkEngine.Instance.Session.Clients` (`IReadOnlyDictionary<ulong, ClientInfo>`); `ClientInfo.PlayerGuid`. Map sender peerId→Guid via `Session.Clients[peerId].PlayerGuid`.

**Game runtime accessors (reflection — mod has NO compile-time game refs)**
- Current level: `GameUtl.CurrentLevel()` (static) → `Component`; `comp.GetComponent(GeoLevelControllerType)`; null when not in geoscape / mid-load. Template: `TimeSyncManager.GetGeoLevel()` (`:189-202`) + `EnsureReflection()` (`:135`).
- Player faction + wallet: `GeoLevelController.PhoenixFaction` (→ `GeoPhoenixFaction`) → `GeoFaction.Wallet` (→ `Wallet`). Path: `level.PhoenixFaction.Wallet`.
- Harmony registration: `harmony.PatchAll(...)` in `MultipleerMain.cs:27`; patch classes in `src/Harmony/` use `[HarmonyPatch]` + `Prepare()`/`TargetMethod()` reflection + `Prefix` returning `bool` (true=run original, false=skip).

**Currency**
- `ResourceType` `[Flags]` (`PhoenixPoint.Common.Core/ResourceType.cs`): `Supplies=1, Materials=2, Tech=4, AICore1=8, AICore2=0x10, AICore3=0x20, Research=0x40, Production=0x80, Mutagen=0x100, LivingCrystals=0x200, Orichalcum=0x400, ProteanMutane=0x800`.
- `Wallet` (`PhoenixPoint.Common.Core/Wallet.cs`): `Dictionary<ResourceType,ResourceUnit> _resources`; mutators `Give/Take/Apply/Clear` all fire `event ResourcesChanged(Wallet, ResourcePack diff, OperationReason)`. `CopyFrom(Wallet)` does NOT fire it.
- TFTV mod currency reuses vanilla `ResourceType` only → syncing the vanilla wallet covers mod currency.

**Geoscape ops (no shared base class)**
- Research: start `Research.AddResearchToQueue(ResearchElement)` (`Research.cs:369`); complete `Research.CompleteResearch(ResearchElement)` (`:576`), event `OnResearchCompleted`.
- Manufacturing: start `ItemManufacturing.ManufactureItem(ManufacturableItem)` (`:169`); complete `ItemManufacturing.FinishManufactureItem(ManufactureQueueItem)` (`:479`), event `OnItemCompleted`.
- Base: construct `GeoPhoenixBase.ConstructFacility(PhoenixFacilityDef, Vector2Int, rotation)` (`:230`); repair `GeoPhoenixBase.RepairFacility(GeoPhoenixFacility)` (`:263`); complete `GeoPhoenixFacility.CompleteFacility()` (`:347`), event `OnFacilityStateUpdated`.
- Events: choice applied via `GeoscapeEvent.CompleteEvent(GeoEventChoice, GeoFaction)` (`GeoscapeEvent.cs:86`). Geoscape event UI forces `PauseGame=true`.

---

## Design model (READ BEFORE CODING)

### Two mechanisms, one engine (`SyncEngine`, mirrors `TimeSyncManager`)

**A. Currency echo (host-authoritative, single chokepoint).**
- Host subscribes `PhoenixFaction.Wallet.ResourcesChanged` → sets `_walletDirty`. `SyncEngine.Tick()` coalesces and, if dirty, broadcasts `WalletSync` (full snapshot of the player wallet: version + list of `(ResourceType,float)`).
- Client `OnWalletSync`: if `version > _lastWalletVersion`, apply each slot as a signed diff via `Wallet.Apply` **inside `SyncApplyScope`** (client never broadcasts wallet → no loop; local UI subscribers update correctly).
- This converges ALL currency changes — action-driven, event rewards, hourly research/production income, mod code, cheats — to host truth, regardless of which of the ~60 call-sites fired them. No per-call-site interception. **This is the deliberate fix for the past "whack-a-mole" failure.**

**B. Action relay (generic discrete-command bus).**
- `ISyncedAction`: a serializable, validatable, replayable discrete command. Registered in `SyncedActionRegistry` by a stable `ushort` id.
- Each game action we sync gets ONE thin Harmony `Prefix` interceptor + ONE `ISyncedAction` class. The bus (relay, sequencing, permission, wire-format) is shared infra — adding an action is small and uniform (fixes the "no shared infra" half of the past failure).
- Flow:
  - **Interceptor `Prefix`** (on the game start/complete method):
    - If `SyncApplyScope.IsApplying` → `return true` (this call IS an engine-driven replay; let it run, do not re-relay).
    - `if (!PermissionGate.Check(category)) { PermissionGate.Notify(category); return false; }` (block disallowed actor).
    - If `engine.IsHost`: `SyncEngine.BroadcastHostAction(action)` then `return true` (host is authority → original runs; clients get the apply).
    - Else (client): `SyncEngine.SendActionRequest(action)` then `return false` (block local; wait for host echo).
  - **Host `OnActionRequest(senderPeerId, payload)`**: resolve actor Guid from `Session.Clients[senderPeerId].PlayerGuid`; re-check `PermissionGate.CheckFor(actorGuid, action.RequiredPermission)`; `action.Validate(rt, actor)`. Pass → apply on host by invoking the real game method inside `SyncApplyScope` (host's own interceptor passes through), assign `sequence`, `BroadcastToAll(ActionApply)`. Fail → `SendToClient(sender, ActionReject(nonce, reason))`.
  - **Client `OnActionApply(payload)`**: read `(actionId, sequence, payload)`; if `sequence <= _lastAppliedSequence` ignore (stale, last-writer-wins ordering); else set `_lastAppliedSequence`, `SyncApplyScope.Enter()`, `action.Apply(rt)`, `SyncApplyScope.Exit()`.
  - **Originator `OnActionReject(payload)`**: surface reason to UI (toast/log v1).

**C. Timed-op progress is NOT streamed.**
- Hourly progression runs on host (authority, clock already synced). Completion methods (`CompleteResearch`, `FinishManufactureItem`, `CompleteFacility`) are intercepted: on **host** → broadcast a completion `ISyncedAction` + run original; on **client** (not in scope) → `return false` (suppress self-completion) so completions arrive only from host. Progress bars on clients are cosmetic (driven by synced clock + last-known queue), acceptable for v1.

### Re-entrancy guard — `SyncApplyScope`
Thread-static counter. `IsApplying` true while the engine replays a remote action or applies a wallet echo. Every interceptor checks it first and passes through. Without this, engine-driven replays would re-trigger interception → infinite relay loop.

### Conflict resolution — last-writer-wins
- Actions: host assigns a strictly increasing `ulong _sequence`. The host's receipt order IS the truth. Clients apply in arrival order and drop any `sequence <= _lastAppliedSequence` (covers out-of-order/duplicate). For competing edits to the same target, the later host-sequenced action wins by construction.
- Wallet: monotonic `ulong version`; clients drop older. Host wallet is always final truth (mechanism A re-converges any client-side action mis-apply).

### Permission — `PermissionGate` (the "core" the user asked for)
- Single static chokepoint mapping action **category** → `CampaignPermission`. Every interceptor calls it FIRST. Today returns true for everyone (FullCommander default) — but the wiring is complete, so the future per-player permission menu only flips `PermissionManager` bits; **no gate code changes**.
- Pause/time control already gated by `ControlTime` in `TimeSyncManager` — leave as-is; `PermissionGate` exposes the same category for consistency.

### `GeoRuntime` — centralized reflection binding
One object that lazily binds + caches the game reflection handles (`GameUtl.CurrentLevel`, `GeoLevelController` type, `PhoenixFaction`, `Wallet`, the action methods) so action `Apply`/`Validate` code and interceptors share one binding surface instead of each re-doing `AccessTools`. Mirrors `TimeSyncManager.EnsureReflection()`.

---

## File structure

**Create:**
- `src/Network/Sync/SyncApplyScope.cs` — thread-static re-entrancy guard.
- `src/Network/Sync/PermissionGate.cs` — category→permission chokepoint + notify hook.
- `src/Network/Sync/ISyncedAction.cs` — action contract + `ActionReader` delegate.
- `src/Network/Sync/SyncedActionIds.cs` — stable `ushort` id constants.
- `src/Network/Sync/SyncedActionRegistry.cs` — id→reader registry.
- `src/Network/Sync/SyncProtocol.cs` — wire codecs for ActionRequest/Apply/Reject + WalletSync (BinaryWriter/Reader; mirrors `TimeSyncProtocol` style but uses BinaryWriter).
- `src/Network/Sync/GeoRuntime.cs` — reflection accessor for live geoscape state + bound game methods.
- `src/Network/Sync/SyncEngine.cs` — subsystem: outbound/inbound, sequencing, wallet echo, registration of all actions.
- `src/Network/Sync/Actions/StartResearchAction.cs`
- `src/Network/Sync/Actions/ResearchCompletedAction.cs`
- `src/Network/Sync/Actions/QueueManufactureAction.cs`
- `src/Network/Sync/Actions/ManufactureCompletedAction.cs`
- `src/Network/Sync/Actions/ConstructFacilityAction.cs`
- `src/Network/Sync/Actions/RepairFacilityAction.cs`
- `src/Network/Sync/Actions/FacilityCompletedAction.cs`
- `src/Network/Sync/Actions/AnswerEventAction.cs`
- `src/Harmony/Sync/ResearchSyncPatches.cs` — interceptors for AddResearchToQueue + CompleteResearch.
- `src/Harmony/Sync/ManufactureSyncPatches.cs` — interceptors for ManufactureItem + FinishManufactureItem.
- `src/Harmony/Sync/BaseSyncPatches.cs` — interceptors for ConstructFacility + RepairFacility + CompleteFacility.
- `src/Harmony/Sync/EventSyncPatches.cs` — interceptor for CompleteEvent.
- `src/Network/Sync/WalletWatcher.cs` — host subscribe to `Wallet.ResourcesChanged` + initial-sync on geoscape active.

**Modify:**
- `src/Network/MessageLayer/PacketType.cs` — add ActionSync group 0x60-0x63.
- `src/Network/NetworkEngine.cs` — create `SyncEngine` in `Initialize()`; tick in `Update()`; add `RouteMessage` cases.
- `src/Validation/PermissionManager.cs` — add `ManageDialogs = 1<<10` to `CampaignPermission`.

**Test (`Multipleer.Tests/`):**
- `SyncProtocolTests.cs` — wire round-trips for every message + every action payload.
- `SyncedActionRegistryTests.cs` — register/read/unknown-id.
- `PermissionGateTests.cs` — category mapping + default-allow + deny when bit cleared.
- `SyncEngineOrderingTests.cs` — last-writer-wins sequence drop + wallet version drop.

---

## Wire protocol (ActionSync group)

`PacketType` additions:
```
// ActionSync 0x60-0x6F
ActionRequest = 0x60,   // client -> host
ActionApply   = 0x61,   // host -> all
ActionReject  = 0x62,   // host -> originator
WalletSync    = 0x63,   // host -> all
```

Payload layouts (all via `BinaryWriter`, big-endian-agnostic — match existing `MessageSerializer` which uses default `BinaryWriter`):
- **ActionRequest**: `u16 actionId, u32 nonce, u16 len, byte[len] actionPayload`
- **ActionApply**: `u16 actionId, u64 sequence, u16 len, byte[len] actionPayload`
- **ActionReject**: `u32 nonce, byte reasonCode, string reason` (`BinaryWriter.Write(string)` length-prefixed)
- **WalletSync**: `u64 version, byte count, count×( i32 resourceType, single value )`

Action payloads (each action's `Write`):
- **StartResearch**: `string researchId` (the `ResearchElement` def id / research name).
- **ResearchCompleted**: `string researchId`.
- **QueueManufacture**: `string itemDefId` (the `ManufacturableItem`/def id).
- **ManufactureCompleted**: `string itemDefId, int queueIndex`.
- **ConstructFacility**: `string baseId, string facilityDefId, i32 gridX, i32 gridY, i32 rotation`.
- **RepairFacility**: `string baseId, string facilityId`.
- **FacilityCompleted**: `string baseId, string facilityId`.
- **AnswerEvent**: `string eventId, i32 choiceIndex`.

> Ids are game def names / persistent identifiers resolvable on every peer (the loaded save is identical post save-transfer). Resolve def→object on `Apply` via the geoscape def repo / faction collections — verify resolver with Serena per action (e.g. find by `ResearchElement.ResearchID`, facility by GUID/instance id). If a stable runtime id is unavailable for an instance (e.g. queue item), fall back to a deterministic index documented in that action's task.

---

## Task 1: `SyncApplyScope` (re-entrancy guard)

**Files:** Create `src/Network/Sync/SyncApplyScope.cs`; Test `Multipleer.Tests/SyncProtocolTests.cs` (add scope test here or a small dedicated test).

- [ ] **Step 1: Write failing test**
```csharp
[Fact]
public void Scope_NestsAndRestores()
{
    Assert.False(SyncApplyScope.IsApplying);
    using (SyncApplyScope.Enter())
    {
        Assert.True(SyncApplyScope.IsApplying);
        using (SyncApplyScope.Enter()) { Assert.True(SyncApplyScope.IsApplying); }
        Assert.True(SyncApplyScope.IsApplying); // still in outer
    }
    Assert.False(SyncApplyScope.IsApplying);
}
```
- [ ] **Step 2: Run → FAIL** (`dotnet test --filter Scope_NestsAndRestores`) — type missing.
- [ ] **Step 3: Implement**
```csharp
using System;
namespace Multipleer.Network.Sync
{
    /// Re-entrancy guard: true while the engine is replaying a remote action or
    /// applying a wallet echo. Interceptors check this FIRST and pass through.
    public static class SyncApplyScope
    {
        [ThreadStatic] private static int _depth;
        public static bool IsApplying => _depth > 0;
        public static IDisposable Enter() { _depth++; return new Handle(); }
        private sealed class Handle : IDisposable
        { private bool _done; public void Dispose() { if (_done) return; _done = true; _depth--; } }
    }
}
```
- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** — `git commit -m "feat(sync): re-entrancy guard for engine-driven replays"`

---

## Task 2: `CampaignPermission.ManageDialogs` + `PermissionGate`

**Files:** Modify `src/Validation/PermissionManager.cs:6-20`; Create `src/Network/Sync/PermissionGate.cs`; Test `Multipleer.Tests/PermissionGateTests.cs`.

- [ ] **Step 1: Add the flag.** In the `[Flags] enum CampaignPermission`, after `FullCommander = 1<<9` add:
```csharp
        ManageDialogs = 1 << 10,
```
- [ ] **Step 2: Write failing tests** (`PermissionGateTests.cs`)
```csharp
using System;
using Multipleer.Network.Sync;
using Multipleer.Validation;
using Xunit;

public class PermissionGateTests
{
    [Fact]
    public void Category_MapsToPermission()
    {
        Assert.Equal(CampaignPermission.ManageResearch, PermissionGate.PermissionFor(ActionCategory.Research));
        Assert.Equal(CampaignPermission.ManageManufacturing, PermissionGate.PermissionFor(ActionCategory.Manufacturing));
        Assert.Equal(CampaignPermission.ManageBases, PermissionGate.PermissionFor(ActionCategory.BaseConstruction));
        Assert.Equal(CampaignPermission.ManageBases, PermissionGate.PermissionFor(ActionCategory.BaseRepair));
        Assert.Equal(CampaignPermission.ManageDialogs, PermissionGate.PermissionFor(ActionCategory.Dialogs));
    }

    [Fact]
    public void FullCommander_AllowsEverything()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermission(g, CampaignPermission.FullCommander, true);
        Assert.True(PermissionGate.CheckFor(g, ActionCategory.Research));
        Assert.True(PermissionGate.CheckFor(g, ActionCategory.Dialogs));
    }

    [Fact]
    public void SpecificBitCleared_Denies()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermissionsRaw(g, (int)CampaignPermission.ManageManufacturing); // only manufacturing, no FullCommander
        Assert.True(PermissionGate.CheckFor(g, ActionCategory.Manufacturing));
        Assert.False(PermissionGate.CheckFor(g, ActionCategory.Research));
    }
}
```
- [ ] **Step 3: Run → FAIL.**
- [ ] **Step 4: Implement** `src/Network/Sync/PermissionGate.cs`
```csharp
using System;
using Multipleer.Network;
using Multipleer.Validation;

namespace Multipleer.Network.Sync
{
    public enum ActionCategory
    {
        Research, Manufacturing, BaseConstruction, BaseRepair,
        Recruitment, Equip, Dialogs, TimeControl
    }

    /// Single permission chokepoint every interceptor calls FIRST.
    /// Today permissive (FullCommander default); future menu only flips PermissionManager bits.
    public static class PermissionGate
    {
        /// Optional UI feedback hook for denied local actions (set by UI layer; no-op if null).
        public static Action<ActionCategory> OnDenied;

        public static CampaignPermission PermissionFor(ActionCategory c)
        {
            switch (c)
            {
                case ActionCategory.Research: return CampaignPermission.ManageResearch;
                case ActionCategory.Manufacturing: return CampaignPermission.ManageManufacturing;
                case ActionCategory.BaseConstruction: return CampaignPermission.ManageBases;
                case ActionCategory.BaseRepair: return CampaignPermission.ManageBases;
                case ActionCategory.Recruitment: return CampaignPermission.ManageRecruitment;
                case ActionCategory.Equip: return CampaignPermission.ManageEquipment;
                case ActionCategory.Dialogs: return CampaignPermission.ManageDialogs;
                case ActionCategory.TimeControl: return CampaignPermission.ControlTime;
                default: return CampaignPermission.FullCommander;
            }
        }

        /// Check a specific player (host uses this for incoming requests).
        public static bool CheckFor(Guid playerGuid, ActionCategory c)
            => PermissionManager.HasCampaignPermission(playerGuid, PermissionFor(c));

        /// Check the LOCAL player (interceptors use this).
        public static bool Check(ActionCategory c)
            => CheckFor(ClientIdentity.PlayerGuid, c);

        public static void Notify(ActionCategory c) => OnDenied?.Invoke(c);
    }
}
```
- [ ] **Step 5: Run → PASS.**
- [ ] **Step 6: Commit** — `git commit -m "feat(sync): PermissionGate chokepoint + ManageDialogs flag"`

---

## Task 3: `ISyncedAction` + `SyncedActionIds` + `SyncedActionRegistry`

**Files:** Create the three files; Test `Multipleer.Tests/SyncedActionRegistryTests.cs`.

- [ ] **Step 1: Implement `ISyncedAction.cs`**
```csharp
using System;
using System.IO;

namespace Multipleer.Network.Sync
{
    /// A discrete, serializable, replayable shared-state mutation.
    public interface ISyncedAction
    {
        ushort ActionId { get; }
        ActionCategory Category { get; }
        void Write(BinaryWriter w);               // payload only (no id/seq/nonce)
        bool Validate(GeoRuntime rt, Guid actor); // host-side pre-apply check
        void Apply(GeoRuntime rt);                // execute real mutation (host authority & client replay)
    }

    /// Reconstructs an action from its payload bytes.
    public delegate ISyncedAction ActionReader(BinaryReader r);
}
```
- [ ] **Step 2: Implement `SyncedActionIds.cs`**
```csharp
namespace Multipleer.Network.Sync
{
    public static class SyncedActionIds
    {
        // Research 1-9
        public const ushort StartResearch = 1;
        public const ushort ResearchCompleted = 2;
        // Manufacturing 10-19
        public const ushort QueueManufacture = 10;
        public const ushort ManufactureCompleted = 11;
        // Base 20-29
        public const ushort ConstructFacility = 20;
        public const ushort RepairFacility = 21;
        public const ushort FacilityCompleted = 22;
        // Events 30-39
        public const ushort AnswerEvent = 30;
    }
}
```
- [ ] **Step 3: Write failing tests** (`SyncedActionRegistryTests.cs`)
```csharp
using System.IO;
using Multipleer.Network.Sync;
using Xunit;

public class SyncedActionRegistryTests
{
    [Fact]
    public void RegisterAndRead_RoundTrips()
    {
        SyncedActionRegistry.Register(9999, r => new FakeAction(r.ReadInt32()));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) w.Write(42);
        ms.Position = 0;
        using var rdr = new BinaryReader(ms);
        var a = SyncedActionRegistry.Read(9999, rdr);
        Assert.IsType<FakeAction>(a);
        Assert.Equal(42, ((FakeAction)a).Value);
    }

    [Fact]
    public void UnknownId_ReturnsNull() => Assert.Null(SyncedActionRegistry.Read(54321, null));
}
```
> `FakeAction` is a minimal test-only `ISyncedAction` stub (id 9999, no-op Validate/Apply) defined in the test file.
- [ ] **Step 4: Run → FAIL.**
- [ ] **Step 5: Implement `SyncedActionRegistry.cs`**
```csharp
using System.Collections.Generic;
using System.IO;

namespace Multipleer.Network.Sync
{
    public static class SyncedActionRegistry
    {
        private static readonly Dictionary<ushort, ActionReader> _readers = new Dictionary<ushort, ActionReader>();

        public static void Register(ushort id, ActionReader reader) => _readers[id] = reader;
        public static bool IsRegistered(ushort id) => _readers.ContainsKey(id);

        public static ISyncedAction Read(ushort id, BinaryReader r)
            => _readers.TryGetValue(id, out var reader) ? reader(r) : null;
    }
}
```
- [ ] **Step 6: Run → PASS.**
- [ ] **Step 7: Commit** — `git commit -m "feat(sync): ISyncedAction contract + id constants + registry"`

---

## Task 4: `SyncProtocol` wire codecs + `PacketType` additions

**Files:** Modify `src/Network/MessageLayer/PacketType.cs`; Create `src/Network/Sync/SyncProtocol.cs`; Test `Multipleer.Tests/SyncProtocolTests.cs`.

- [ ] **Step 1: Add PacketTypes.** In `PacketType.cs` after the Management group add:
```csharp
        // ActionSync 0x60-0x6F
        ActionRequest = 0x60,
        ActionApply   = 0x61,
        ActionReject  = 0x62,
        WalletSync    = 0x63,
```
- [ ] **Step 2: Write failing tests** (`SyncProtocolTests.cs`)
```csharp
using System.Collections.Generic;
using Multipleer.Network.Sync;
using Xunit;

public class SyncProtocolTests
{
    [Fact]
    public void ActionRequest_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3 };
        var bytes = SyncProtocol.EncodeActionRequest(SyncedActionIds.StartResearch, 0xABCDu, payload);
        Assert.True(SyncProtocol.TryDecodeActionRequest(bytes, out var id, out var nonce, out var pl));
        Assert.Equal(SyncedActionIds.StartResearch, id);
        Assert.Equal(0xABCDu, nonce);
        Assert.Equal(payload, pl);
    }

    [Fact]
    public void ActionApply_RoundTrips()
    {
        var payload = new byte[] { 9 };
        var bytes = SyncProtocol.EncodeActionApply(SyncedActionIds.ConstructFacility, 777UL, payload);
        Assert.True(SyncProtocol.TryDecodeActionApply(bytes, out var id, out var seq, out var pl));
        Assert.Equal(SyncedActionIds.ConstructFacility, id);
        Assert.Equal(777UL, seq);
        Assert.Equal(payload, pl);
    }

    [Fact]
    public void ActionReject_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeActionReject(0x1234u, 7, "no funds");
        Assert.True(SyncProtocol.TryDecodeActionReject(bytes, out var nonce, out var code, out var reason));
        Assert.Equal(0x1234u, nonce); Assert.Equal((byte)7, code); Assert.Equal("no funds", reason);
    }

    [Fact]
    public void WalletSync_RoundTrips()
    {
        var slots = new List<(int, float)> { (1, 100f), (0x40, 12.5f), (0x800, -3f) };
        var bytes = SyncProtocol.EncodeWalletSync(55UL, slots);
        Assert.True(SyncProtocol.TryDecodeWalletSync(bytes, out var ver, out var outSlots));
        Assert.Equal(55UL, ver);
        Assert.Equal(slots, outSlots);
    }
}
```
- [ ] **Step 3: Run → FAIL.**
- [ ] **Step 4: Implement `SyncProtocol.cs`** (BinaryWriter/Reader; mirror `MessageSerializer` idioms)
```csharp
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multipleer.Network.Sync
{
    /// Wire codecs for ActionSync payloads. Header/envelope handled by NetworkMessage.
    public static class SyncProtocol
    {
        public static byte[] EncodeActionRequest(ushort actionId, uint nonce, byte[] payload)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(actionId); w.Write(nonce);
            w.Write((ushort)payload.Length); w.Write(payload);
            return ms.ToArray();
        }
        public static bool TryDecodeActionRequest(byte[] data, out ushort actionId, out uint nonce, out byte[] payload)
        {
            actionId = 0; nonce = 0; payload = null;
            try { using var ms = new MemoryStream(data); using var r = new BinaryReader(ms, Encoding.UTF8);
                actionId = r.ReadUInt16(); nonce = r.ReadUInt32();
                payload = r.ReadBytes(r.ReadUInt16()); return true; }
            catch { return false; }
        }

        public static byte[] EncodeActionApply(ushort actionId, ulong sequence, byte[] payload)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(actionId); w.Write(sequence);
            w.Write((ushort)payload.Length); w.Write(payload);
            return ms.ToArray();
        }
        public static bool TryDecodeActionApply(byte[] data, out ushort actionId, out ulong sequence, out byte[] payload)
        {
            actionId = 0; sequence = 0; payload = null;
            try { using var ms = new MemoryStream(data); using var r = new BinaryReader(ms, Encoding.UTF8);
                actionId = r.ReadUInt16(); sequence = r.ReadUInt64();
                payload = r.ReadBytes(r.ReadUInt16()); return true; }
            catch { return false; }
        }

        public static byte[] EncodeActionReject(uint nonce, byte reasonCode, string reason)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(nonce); w.Write(reasonCode); w.Write(reason ?? "");
            return ms.ToArray();
        }
        public static bool TryDecodeActionReject(byte[] data, out uint nonce, out byte reasonCode, out string reason)
        {
            nonce = 0; reasonCode = 0; reason = null;
            try { using var ms = new MemoryStream(data); using var r = new BinaryReader(ms, Encoding.UTF8);
                nonce = r.ReadUInt32(); reasonCode = r.ReadByte(); reason = r.ReadString(); return true; }
            catch { return false; }
        }

        public static byte[] EncodeWalletSync(ulong version, List<(int type, float value)> slots)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(version); w.Write((byte)slots.Count);
            foreach (var (t, v) in slots) { w.Write(t); w.Write(v); }
            return ms.ToArray();
        }
        public static bool TryDecodeWalletSync(byte[] data, out ulong version, out List<(int type, float value)> slots)
        {
            version = 0; slots = null;
            try { using var ms = new MemoryStream(data); using var r = new BinaryReader(ms, Encoding.UTF8);
                version = r.ReadUInt64(); int n = r.ReadByte(); slots = new List<(int, float)>(n);
                for (int i = 0; i < n; i++) slots.Add((r.ReadInt32(), r.ReadSingle())); return true; }
            catch { return false; }
        }
    }
}
```
- [ ] **Step 5: Run → PASS.**
- [ ] **Step 6: Commit** — `git commit -m "feat(sync): ActionSync packet types + SyncProtocol wire codecs"`

---

## Task 5: `GeoRuntime` (reflection accessor)

**Files:** Create `src/Network/Sync/GeoRuntime.cs`. (No unit test — reflection against live game; verify via in-game smoke later. Keep methods null-safe.)

- [ ] **Step 1: Implement** — mirror `TimeSyncManager.EnsureReflection()/GetGeoLevel()`. Bind once, cache. Provide:
```csharp
using System;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// Centralized reflection surface for live geoscape state + bound action methods.
    /// All game types resolved by name (mod has no compile-time game refs).
    public sealed class GeoRuntime
    {
        private static GeoRuntime _instance;
        public static GeoRuntime Instance => _instance ??= new GeoRuntime();

        private Type _geoLevelType, _gameUtlType;
        private System.Reflection.MethodInfo _currentLevel;
        private bool _ready;

        private GeoRuntime() => EnsureReflection();

        private void EnsureReflection()
        {
            if (_ready) return;
            _geoLevelType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            _gameUtlType  = AccessTools.TypeByName("Base.Core.GameUtl") ?? AccessTools.TypeByName("GameUtl");
            _currentLevel = _gameUtlType != null ? AccessTools.Method(_gameUtlType, "CurrentLevel") : null;
            _ready = _geoLevelType != null && _currentLevel != null;
        }

        /// Live GeoLevelController, or null if not in geoscape / mid-load.
        public object GeoLevel()
        {
            EnsureReflection();
            if (!_ready) return null;
            var level = _currentLevel.Invoke(null, null);
            return level is Component c ? c.GetComponent(_geoLevelType) : null;
        }

        public object PhoenixFaction()
        {
            var geo = GeoLevel();
            return geo == null ? null : AccessTools.Property(_geoLevelType, "PhoenixFaction")?.GetValue(geo);
        }

        /// The player faction Wallet, or null.
        public object Wallet()
        {
            var fac = PhoenixFaction();
            return fac == null ? null : AccessTools.Property(fac.GetType(), "Wallet")?.GetValue(fac);
        }

        public bool IsGeoscapeActive => GeoLevel() != null;
    }
}
```
> Worker: verify `GameUtl` namespace + `CurrentLevel` signature with Serena before finalizing the `TypeByName` strings. Add bound `MethodInfo` fields for each action method as Tasks 6-9 need them (e.g. `Research.AddResearchToQueue`), each lazily bound + cached here so actions don't re-reflect.
- [ ] **Step 2: Build** (`dotnet build`) — must compile.
- [ ] **Step 3: Commit** — `git commit -m "feat(sync): GeoRuntime reflection accessor for live geoscape"`

---

## Task 6: `SyncEngine` (subsystem core) + NetworkEngine wiring + ordering tests

**Files:** Create `src/Network/Sync/SyncEngine.cs`; Modify `src/Network/NetworkEngine.cs` (`Initialize`, `Update`, `RouteMessage`); Test `Multipleer.Tests/SyncEngineOrderingTests.cs`.

The engine must be unit-testable for the ordering logic WITHOUT the network. Extract pure decision helpers as static/internal methods so tests don't need a live `NetworkEngine`.

- [ ] **Step 1: Write failing ordering tests** (`SyncEngineOrderingTests.cs`)
```csharp
using Multipleer.Network.Sync;
using Xunit;

public class SyncEngineOrderingTests
{
    [Fact]
    public void Apply_DropsStaleOrDuplicateSequence()
    {
        var t = new SequenceTracker();
        Assert.True(t.ShouldApply(1));   t.Mark(1);
        Assert.True(t.ShouldApply(2));   t.Mark(2);
        Assert.False(t.ShouldApply(2));  // duplicate
        Assert.False(t.ShouldApply(1));  // stale
        Assert.True(t.ShouldApply(3));
    }

    [Fact]
    public void Wallet_DropsOlderVersion()
    {
        var t = new SequenceTracker();
        Assert.True(t.ShouldApplyWallet(10)); t.MarkWallet(10);
        Assert.False(t.ShouldApplyWallet(10));
        Assert.False(t.ShouldApplyWallet(9));
        Assert.True(t.ShouldApplyWallet(11));
    }
}
```
- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement `SequenceTracker`** (inside `SyncEngine.cs` or a small own file `src/Network/Sync/SequenceTracker.cs`)
```csharp
namespace Multipleer.Network.Sync
{
    /// Last-writer-wins ordering: drop stale/duplicate action sequences + old wallet versions.
    public sealed class SequenceTracker
    {
        private ulong _lastApplied;     // actions
        private ulong _lastWallet;      // wallet versions
        public bool ShouldApply(ulong seq) => seq > _lastApplied;
        public void Mark(ulong seq) { if (seq > _lastApplied) _lastApplied = seq; }
        public bool ShouldApplyWallet(ulong ver) => ver > _lastWallet;
        public void MarkWallet(ulong ver) { if (ver > _lastWallet) _lastWallet = ver; }
    }
}
```
- [ ] **Step 4: Run ordering tests → PASS.**
- [ ] **Step 5: Implement `SyncEngine.cs`** — full subsystem. Key shape:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multipleer.Network.MessageLayer;

namespace Multipleer.Network.Sync
{
    public sealed class SyncEngine
    {
        private readonly NetworkEngine _engine;
        private readonly SequenceTracker _tracker = new SequenceTracker();
        private ulong _hostSequence;          // host-assigned, monotonic
        private ulong _walletVersion;         // host-assigned, monotonic
        private uint _nonceCounter;           // client request correlation
        private bool _walletDirty;            // host: wallet changed since last flush
        private readonly Dictionary<uint, ISyncedAction> _pending = new Dictionary<uint, ISyncedAction>();

        public SyncEngine(NetworkEngine engine)
        {
            _engine = engine;
            SyncRegistration.RegisterAll();   // registers every action reader (Tasks 7-10)
        }

        // ---- outbound (called by interceptors) ----
        public void SendActionRequest(ISyncedAction a)
        {
            uint nonce = ++_nonceCounter;
            _pending[nonce] = a;
            var payload = WriteAction(a);
            _engine.SendToHost(new NetworkMessage(PacketType.ActionRequest,
                SyncProtocol.EncodeActionRequest(a.ActionId, nonce, payload)));
        }

        public void BroadcastHostAction(ISyncedAction a)   // host: original already ran (or will)
        {
            ulong seq = ++_hostSequence;
            _tracker.Mark(seq);
            var payload = WriteAction(a);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ActionApply,
                SyncProtocol.EncodeActionApply(a.ActionId, seq, payload)));
        }

        // ---- inbound: host ----
        public void OnActionRequest(ulong senderPeerId, byte[] data)
        {
            if (!_engine.IsHost) return;
            if (!SyncProtocol.TryDecodeActionRequest(data, out var id, out var nonce, out var payload)) return;
            var action = ReadAction(id, payload);
            if (action == null) return;

            Guid actor = ResolveActor(senderPeerId);
            var rt = GeoRuntime.Instance;
            if (!PermissionGate.CheckFor(actor, action.Category) || !action.Validate(rt, actor))
            {
                _engine.SendToClient(senderPeerId, new NetworkMessage(PacketType.ActionReject,
                    SyncProtocol.EncodeActionReject(nonce, 1, "rejected")));
                return;
            }
            using (SyncApplyScope.Enter()) action.Apply(rt);   // host executes authoritative mutation
            ulong seq = ++_hostSequence; _tracker.Mark(seq);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ActionApply,
                SyncProtocol.EncodeActionApply(id, seq, payload)));
        }

        // ---- inbound: client ----
        public void OnActionApply(byte[] data)
        {
            if (!SyncProtocol.TryDecodeActionApply(data, out var id, out var seq, out var payload)) return;
            if (!_tracker.ShouldApply(seq)) return;       // last-writer-wins / dedupe
            _tracker.Mark(seq);
            var action = ReadAction(id, payload);
            if (action == null) return;
            using (SyncApplyScope.Enter()) action.Apply(GeoRuntime.Instance);
        }

        public void OnActionReject(byte[] data)
        {
            if (!SyncProtocol.TryDecodeActionReject(data, out var nonce, out var code, out var reason)) return;
            _pending.Remove(nonce);
            // v1: log; UI hook later
        }

        // ---- currency ----
        public void MarkWalletDirty() => _walletDirty = true;     // host WalletWatcher callback

        public void OnWalletSync(byte[] data)                     // client
        {
            if (_engine.IsHost) return;
            if (!SyncProtocol.TryDecodeWalletSync(data, out var ver, out var slots)) return;
            if (!_tracker.ShouldApplyWallet(ver)) return;
            _tracker.MarkWallet(ver);
            using (SyncApplyScope.Enter()) WalletApplier.Apply(GeoRuntime.Instance, slots);
        }

        // ---- tick (host wallet flush; called from NetworkEngine.Update) ----
        public void Tick()
        {
            if (_engine.IsHost && _walletDirty)
            {
                _walletDirty = false;
                var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
                if (slots != null)
                    _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                        SyncProtocol.EncodeWalletSync(++_walletVersion, slots)));
            }
        }

        public void BroadcastFullWallet()   // call on geoscape-active / new client join
        {
            if (!_engine.IsHost) return;
            var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
            if (slots != null)
                _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                    SyncProtocol.EncodeWalletSync(++_walletVersion, slots)));
        }

        private static byte[] WriteAction(ISyncedAction a)
        { using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, Encoding.UTF8); a.Write(w); return ms.ToArray(); }

        private static ISyncedAction ReadAction(ushort id, byte[] payload)
        { using var ms = new MemoryStream(payload); using var r = new BinaryReader(ms, Encoding.UTF8); return SyncedActionRegistry.Read(id, r); }

        private Guid ResolveActor(ulong peerId)
            => _engine.Session != null && _engine.Session.Clients.TryGetValue(peerId, out var ci) ? ci.PlayerGuid : Guid.Empty;
    }
}
```
> `SyncRegistration.RegisterAll()`, `WalletApplier`, and each action class are delivered in Tasks 7-10. Until those exist, stub `SyncRegistration.RegisterAll()` as empty and `WalletApplier` per Task 10 (do Task 10's `WalletApplier` first if compiling Task 6 standalone — see ordering note at end). Verify `_engine.Session` accessor name against `NetworkEngine`.
- [ ] **Step 6: Wire into `NetworkEngine`.**
  - In `Initialize()` after `TimeSync = new TimeSyncManager(this);` add `Sync = new SyncEngine(this);` and declare `public SyncEngine Sync { get; private set; }`.
  - In `Update()` after `TimeSync?.Tick();` add `Sync?.Tick();`.
  - In `RouteMessage` switch add:
```csharp
                case PacketType.ActionRequest: Sync?.OnActionRequest(msg.SenderSteamId, msg.Payload); break;
                case PacketType.ActionApply:   Sync?.OnActionApply(msg.Payload); break;
                case PacketType.ActionReject:  Sync?.OnActionReject(msg.Payload); break;
                case PacketType.WalletSync:    Sync?.OnWalletSync(msg.Payload); break;
```
> Verify the sender-id field name on `NetworkMessage` (`SenderSteamId`) and that it equals the transport peerId used as `Session.Clients` key.
- [ ] **Step 7: Build → must compile** (with stubs for not-yet-built pieces).
- [ ] **Step 8: Commit** — `git commit -m "feat(sync): SyncEngine subsystem + NetworkEngine wiring + ordering"`

---

## Task 7: Currency — `WalletApplier` + `WalletWatcher` (host echo)

**Files:** Create `src/Network/Sync/WalletApplier.cs`, `src/Network/Sync/WalletWatcher.cs`; Test in `SyncProtocolTests.cs` (snapshot list shape only — apply is reflection, smoke-test in game).

- [ ] **Step 1: Implement `WalletApplier.cs`** — snapshot/apply the player wallet via reflection.
```csharp
using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Multipleer.Network.Sync
{
    /// Reflection bridge to the player Wallet: snapshot all ResourceType slots (host)
    /// and apply a target snapshot as signed diffs (client).
    public static class WalletApplier
    {
        // The 12 vanilla ResourceType flag values (mod currency reuses these).
        private static readonly int[] Types = { 1,2,4,8,0x10,0x20,0x40,0x80,0x100,0x200,0x400,0x800 };

        public static List<(int type, float value)> Snapshot(GeoRuntime rt)
        {
            var wallet = rt.Wallet(); if (wallet == null) return null;
            var list = new List<(int, float)>(Types.Length);
            foreach (var t in Types) list.Add((t, GetAmount(wallet, t)));
            return list;
        }

        public static void Apply(GeoRuntime rt, List<(int type, float value)> target)
        {
            var wallet = rt.Wallet(); if (wallet == null) return;
            foreach (var (t, v) in target)
            {
                float cur = GetAmount(wallet, t);
                float diff = v - cur;
                if (Math.Abs(diff) > 0.0001f) ApplyDiff(wallet, t, diff);
            }
        }

        // --- reflection helpers (verify exact signatures with Serena) ---
        private static float GetAmount(object wallet, int type)
        {
            // Wallet has a getter for a ResourceType amount; e.g. GetResourceAmount(ResourceType) or indexer.
            // Resolve once + cache in real impl. Pseudocode: invoke bound method with boxed enum value.
            return WalletReflection.GetAmount(wallet, type);
        }
        private static void ApplyDiff(object wallet, int type, float diff)
        {
            // Build ResourceUnit(type, diff) + call Wallet.Apply(ResourceUnit, OperationReason) under SyncApplyScope.
            WalletReflection.ApplyDiff(wallet, type, diff);
        }
    }
}
```
> **Worker:** create `WalletReflection` (same folder) binding the real getter + `Apply(ResourceUnit, OperationReason)` + the `ResourceUnit` ctor + an `OperationReason` value (use a benign reason; verify enum members with Serena in `PhoenixPoint.Common.Core`). Cache `MethodInfo`/`ConstructorInfo`. Client-side `Apply` runs under `SyncApplyScope` (set by caller `OnWalletSync`) so any interceptors pass through.
- [ ] **Step 2: Implement `WalletWatcher.cs`** — host subscribe to `Wallet.ResourcesChanged`.
```csharp
using System;
using HarmonyLib;

namespace Multipleer.Network.Sync
{
    /// Host-only: subscribes to the player Wallet.ResourcesChanged and marks the
    /// SyncEngine wallet dirty (coalesced flush in SyncEngine.Tick). Also pushes a
    /// full wallet snapshot when geoscape becomes active.
    public static class WalletWatcher
    {
        private static Delegate _handler;
        private static object _wallet;

        public static void Attach(NetworkEngine engine)
        {
            if (!engine.IsHost) return;
            var wallet = GeoRuntime.Instance.Wallet();
            if (wallet == null || ReferenceEquals(wallet, _wallet)) return;
            Detach();
            _wallet = wallet;
            var evt = AccessTools.Property(wallet.GetType(), null); // resolve event 'ResourcesChanged' via EventInfo
            // Subscribe a handler matching ResourcesChangedEventHandler(Wallet, ResourcePack, OperationReason)
            _handler = WalletReflection.SubscribeResourcesChanged(wallet, () => engine.Sync?.MarkWalletDirty());
            engine.Sync?.BroadcastFullWallet();
        }

        public static void Detach()
        {
            if (_wallet != null && _handler != null) WalletReflection.UnsubscribeResourcesChanged(_wallet, _handler);
            _wallet = null; _handler = null;
        }
    }
}
```
> **Worker:** Subscribing to a delegate-typed event via reflection: build a matching delegate (use a small concrete handler method with signature `(object, object, object)` is NOT enough — the event delegate type is `Wallet.ResourcesChangedEventHandler`; create the delegate via `Delegate.CreateDelegate` against that type, or wrap with a HarmonyX-friendly approach). Implement in `WalletReflection`. Verify the event name `ResourcesChanged` and delegate signature with Serena.
- [ ] **Step 3: Call `WalletWatcher.Attach(engine)`** at the right lifecycle point — where the mod knows geoscape is active and the session is established. Reuse the same trigger the loading/`SessionBegin` path uses (find via Serena: where `ClientLoaded`/`SessionBegin`/world-load completes). Also call on each new `ClientReady` (host re-broadcasts full wallet so late joiners converge). Add `WalletWatcher.Detach()` on session end / disconnect.
- [ ] **Step 4: Build → compile.**
- [ ] **Step 5: Commit** — `git commit -m "feat(sync): host-authoritative currency echo (wallet snapshot + watcher)"`

---

## Task 8: Research module (start + completion)

**Files:** Create `src/Network/Sync/Actions/StartResearchAction.cs`, `ResearchCompletedAction.cs`; `src/Harmony/Sync/ResearchSyncPatches.cs`.

- [ ] **Step 1: `StartResearchAction.cs`**
```csharp
using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    public sealed class StartResearchAction : ISyncedAction
    {
        private readonly string _researchId;
        public StartResearchAction(string researchId) { _researchId = researchId; }
        public ushort ActionId => SyncedActionIds.StartResearch;
        public ActionCategory Category => ActionCategory.Research;
        public void Write(BinaryWriter w) => w.Write(_researchId ?? "");
        public static ISyncedAction Read(BinaryReader r) => new StartResearchAction(r.ReadString());
        public bool Validate(GeoRuntime rt, Guid actor) => !string.IsNullOrEmpty(_researchId) && rt.IsGeoscapeActive;
        public void Apply(GeoRuntime rt) => ResearchReflection.AddToQueue(rt, _researchId);
    }
}
```
- [ ] **Step 2: `ResearchCompletedAction.cs`** — same shape, `ActionId => ResearchCompleted`, `Apply` → `ResearchReflection.Complete(rt, _researchId)`.
- [ ] **Step 3: `ResearchReflection`** (in same folder or `src/Network/Sync/`) — bind `Research` type + `AddResearchToQueue(ResearchElement)` (`Research.cs:369`) + `CompleteResearch(ResearchElement)` (`:576`), and a resolver from `researchId` → `ResearchElement` (find via the faction's research collection by `ResearchID`). Verify exact members with Serena.
- [ ] **Step 4: `ResearchSyncPatches.cs`** — two Harmony patch classes following `TimeControlPatches.cs` pattern:
  - `AddResearchToQueuePatch` Prefix on `Research.AddResearchToQueue`:
```csharp
static bool Prefix(object __instance, object research /* ResearchElement */)
{
    if (SyncApplyScope.IsApplying) return true;
    var engine = NetworkEngine.Instance; if (engine == null || !engine.IsActiveSession) return true;
    if (!PermissionGate.Check(ActionCategory.Research)) { PermissionGate.Notify(ActionCategory.Research); return false; }
    string id = ResearchReflection.GetId(research);
    var action = new StartResearchAction(id);
    if (engine.IsHost) { engine.Sync.BroadcastHostAction(action); return true; }
    engine.Sync.SendActionRequest(action); return false;
}
```
  - `CompleteResearchPatch` Prefix on `Research.CompleteResearch`: host → broadcast `ResearchCompletedAction` + `return true`; client → if `SyncApplyScope.IsApplying` `return true` else `return false` (suppress self-completion; host drives it).
> Verify `IsActiveSession` (or equivalent "are we in a multiplayer session" flag) on `NetworkEngine`; if none exists, add a simple `public bool IsActiveSession => Session != null && Session.IsConnected` guard (check Session API). Without this guard, single-player play would be intercepted — must early-return when not networked.
- [ ] **Step 5: Build → compile.**
- [ ] **Step 6: Commit** — `git commit -m "feat(sync): research start + completion sync"`

---

## Task 9: Manufacturing + Base (construct/repair/complete) modules

**Files:** `Actions/QueueManufactureAction.cs`, `ManufactureCompletedAction.cs`, `ConstructFacilityAction.cs`, `RepairFacilityAction.cs`, `FacilityCompletedAction.cs`; `src/Harmony/Sync/ManufactureSyncPatches.cs`, `src/Harmony/Sync/BaseSyncPatches.cs`; reflection helpers `ManufactureReflection`, `BaseReflection`.

- [ ] **Step 1: Manufacturing actions** — `QueueManufactureAction(string itemDefId)` Category `Manufacturing`, Apply → `ManufactureReflection.Queue(rt, itemDefId)` (binds `ItemManufacturing.ManufactureItem`, `:169`; resolver itemDefId→`ManufacturableItem`). `ManufactureCompletedAction(string itemDefId, int queueIndex)` Apply → `ManufactureReflection.Complete(rt, itemDefId, queueIndex)` (binds `FinishManufactureItem`, `:479`).
- [ ] **Step 2: Base actions**
  - `ConstructFacilityAction(string baseId, string facilityDefId, int x, int y, int rot)` Category `BaseConstruction`, Apply → `BaseReflection.Construct(rt, baseId, facilityDefId, x, y, rot)` (binds `GeoPhoenixBase.ConstructFacility`, `:230`).
  - `RepairFacilityAction(string baseId, string facilityId)` Category `BaseRepair`, Apply → `BaseReflection.Repair(rt, baseId, facilityId)` (binds `GeoPhoenixBase.RepairFacility`, `:263`).
  - `FacilityCompletedAction(string baseId, string facilityId)` Category `BaseConstruction`, Apply → `BaseReflection.Complete(rt, baseId, facilityId)` (binds `GeoPhoenixFacility.CompleteFacility`, `:347`).
  > Resolvers: base by a stable id (find `GeoPhoenixBase` id/GUID via Serena), facility by instance id within the base layout, facility def by def name. Verify all with Serena; if no stable facility instance id exists, key by grid position `(x,y)` within the base.
- [ ] **Step 3: `ManufactureSyncPatches.cs`** — `ManufactureItemPatch` (start, Category Manufacturing) + `FinishManufactureItemPatch` (completion: host broadcast + run; client suppress unless in scope). Same Prefix shape as Task 8 Step 4.
- [ ] **Step 4: `BaseSyncPatches.cs`** — `ConstructFacilityPatch` (Category BaseConstruction), `RepairFacilityPatch` (Category BaseRepair), `CompleteFacilityPatch` (completion: host broadcast + run; client suppress unless in scope).
- [ ] **Step 5: Build → compile.**
- [ ] **Step 6: Commit** — `git commit -m "feat(sync): manufacturing + base construction/repair sync"`

---

## Task 10: Events/dialog choice module + `SyncRegistration`

**Files:** `Actions/AnswerEventAction.cs`; `src/Harmony/Sync/EventSyncPatches.cs`; `EventReflection`; `src/Network/Sync/SyncRegistration.cs`.

- [ ] **Step 1: `AnswerEventAction.cs`** — `AnswerEventAction(string eventId, int choiceIndex)` Category `Dialogs`. Apply → `EventReflection.CompleteEvent(rt, eventId, choiceIndex)` (binds `GeoscapeEvent.CompleteEvent(GeoEventChoice, GeoFaction)`, `GeoscapeEvent.cs:86`; resolve the active `GeoscapeEvent` by `eventId` from the event system record, resolve choice by index in `EventData.Choices`, pass `PhoenixFaction`).
- [ ] **Step 2: `EventSyncPatches.cs`** — `CompleteEventPatch` Prefix on `GeoscapeEvent.CompleteEvent`:
  - If `SyncApplyScope.IsApplying` → `return true`.
  - Not networked → `return true`.
  - `if (!PermissionGate.Check(ActionCategory.Dialogs)) { Notify; return false; }`
  - Build `AnswerEventAction(eventId, choiceIndex)` (extract eventId + the chosen index from args/instance via `EventReflection`).
  - Host → `BroadcastHostAction` + `return true`; client → `SendActionRequest` + `return false`.
  > Geoscape event UI force-pauses; the event resolves through the synced clock + this action. The host's choice is authoritative; clients see the same outcome applied. If both pick simultaneously, last host-sequenced wins (the earlier one's UI just closes on apply).
- [ ] **Step 3: `SyncRegistration.cs`** — register every action reader (called from `SyncEngine` ctor):
```csharp
using Multipleer.Network.Sync.Actions;
namespace Multipleer.Network.Sync
{
    public static class SyncRegistration
    {
        private static bool _done;
        public static void RegisterAll()
        {
            if (_done) return; _done = true;
            SyncedActionRegistry.Register(SyncedActionIds.StartResearch, StartResearchAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ResearchCompleted, ResearchCompletedAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.QueueManufacture, QueueManufactureAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ManufactureCompleted, ManufactureCompletedAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ConstructFacility, ConstructFacilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.RepairFacility, RepairFacilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.FacilityCompleted, FacilityCompletedAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.AnswerEvent, AnswerEventAction.Read);
        }
    }
}
```
- [ ] **Step 4: Build → compile.**
- [ ] **Step 5: Commit** — `git commit -m "feat(sync): event-choice sync + action registration"`

---

## Task 11: Full build + test pass + docs

- [ ] **Step 1:** `dotnet build` (whole solution) → 0 errors.
- [ ] **Step 2:** `dotnet test` → all green (Sync* tests + existing TimeSync tests untouched).
- [ ] **Step 3:** Update `docs/research/00-current-state.md` + `docs/README.md` with a short "Action-Sync engine" section: the two mechanisms, where to add a new synced action (action class + registry entry + interceptor + category), the permission-gate chokepoint. (SCRIBE-role.)
- [ ] **Step 4: Commit** — `git commit -m "docs(sync): document action-sync engine + extension recipe"`

---

## Build-order note for workers

Task 6 (`SyncEngine`) references `WalletApplier` (Task 7) and `SyncRegistration` (Task 10). To keep each task compiling: when doing Task 6, add a temporary empty `SyncRegistration.RegisterAll()` and minimal `WalletApplier.Snapshot`/`Apply` stubs (return null / no-op), then flesh them out in Tasks 7 & 10. Alternatively implement in order 1→5, then 7's `WalletApplier`+`WalletReflection`, then 6, then 8→10. Either way every commit must build.

## Self-review checklist (done by plan author)
- Spec coverage: currency ✓(T7) incl. mod currency (vanilla ResourceType) ✓; research ✓(T8); research completion ✓(T8); repair ✓(T9); base construction ✓(T9); construction completion ✓(T9 FacilityCompleted); production/manufacturing ✓(T9); events/dialog choices ✓(T10); permission-gating from day one ✓(T2 gate + every interceptor); pause/time control — already gated, category exposed ✓; last-writer-wins ✓(T6 SequenceTracker); client-can-also-act ✓(client interceptor → SendActionRequest); engine/core for future permission menu ✓(PermissionGate + categories, flip bits only).
- Type consistency: `ISyncedAction.Apply(GeoRuntime)`, `Validate(GeoRuntime, Guid)`, `Write(BinaryWriter)`, static `Read(BinaryReader)` — used consistently T3/T6/T8/T9/T10. `SequenceTracker.ShouldApply/Mark/ShouldApplyWallet/MarkWallet` consistent T6. `PermissionGate.Check/CheckFor/PermissionFor/Notify` consistent T2/T6/T8-10.
- Open verification points (worker must confirm via Serena before binding, flagged inline): `GameUtl` namespace; `Wallet` amount-getter + `ResourcesChanged` event delegate; `OperationReason` member; def→object resolvers for research/item/facility/base/event; `NetworkEngine.IsActiveSession`/`Session.IsConnected`; `NetworkMessage.SenderSteamId` == `Session.Clients` key.
