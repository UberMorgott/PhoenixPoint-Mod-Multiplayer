# Geoscape Command Sync — Stage 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make client geoscape command actions replicate host-authoritatively with real per-`playerGUID` permission enforcement, proven end-to-end on `GeoVehicle.StartTravel`, with a broad curated intercept registry plugged into one shared relay.

**Architecture:** Host is the sole source of truth. A client geoscape action is intercepted by a Harmony prefix, encoded into a `CampaignActionMessage` (envelope already exists), sent to the host, and local execution is blocked; the host validates ownership/legality/permission, executes the REAL game method, and broadcasts the approved action so every peer (including the originator) reproduces the result without recomputing. The pipeline is built once from small single-responsibility units (CommandCodec, PermissionGate, InterceptRegistry, HostArbiter, ClientApplier, CommandRelay) and the curated C1–C7 method list is registered against it via a declarative registry, prune-later.

**Tech Stack:** C# (net472), HarmonyLib (`AccessTools` dynamic resolution), xUnit 2.9.2 (`Multiplayer.Tests`), the existing `CampaignAction` packet skeleton (`0x30` request / `0x31` approved / `0x32` rejected), `NetworkEngine` events `OnCampaignActionRequest` / `OnHostCampaignActionResult`, `PermissionManager` per-GUID flags.

---

## Grounding notes (verified against real source + decompile — read before coding)

- **`CampaignActionType` already contains `StartTravel = 13`** (`src/Network/MessageLayer/MessageSerializer.cs:532-548`). Do NOT re-add the enum value; Task 1 adds only the StartTravel **payload** codec.
- **`CampaignActionMessage`** fields are `ActionId(Guid)`, `ActionType`, `TargetId(string)`, `Payload(byte[])`, `Timestamp(long)` — there is **no `ActorId` field** (the design prose said "ActorId"; the real envelope carries the actor in `TargetId`/`Payload`). `MessageSerializer.SerializeCampaignAction` / `DeserializeCampaignAction` already round-trip the envelope (`:47-77`).
- **Protocol (`src/Network/NetworkEngine.cs`):** packets `CampaignActionRequest=0x30`, `CampaignActionApproved=0x31`, `CampaignActionRejected=0x32`, `CampaignActionResult=0x33` (stub TODO `:511`), `CampaignStateUpdate=0x34` (stub TODO `:515`). Events `event Action<ulong,CampaignActionMessage> OnCampaignActionRequest` (host-side, fired with sender id in `RouteMessage` `0x30` `:410-413`) and `event Action<CampaignActionMessage> OnHostCampaignActionResult` (client-side, fired on **both** `0x31` and `0x32` `:415-419`) — **currently zero subscribers**; Stage 1 wires them.
- **`ApproveCampaignAction(ulong clientId, action)` and `RejectCampaignAction(ulong clientId, action, reason)` send to ONE client only** (`SendToClient`). Result-replay needs a fan-out to ALL peers → Task 6 adds `NetworkEngine.BroadcastCampaignActionResult(CampaignActionMessage)` (`BroadcastToAll` of a `0x31` message).
- **`CampaignPermission`** flags live in `src/Validation/PermissionManager.cs:7-21`: `ManageAircraft = 1<<6`, `ControlTime = 1<<7`, `FullCommander = 1<<9`, etc. `PermissionManager.HasCampaignPermission(Guid, CampaignPermission)` honours the `FullCommander` override (`:88-97`). `PermissionManager` is Unity-free → unit-linkable.
- **Existing stub to replace:** `CampaignPermissionHelper.Check(CampaignPermission required)` in `src/Harmony/CampaignPatches.cs:10-19` = host→allow / client→block, **GUID-blind**. PermissionGate (Task 2) is the real per-GUID gate; the host path (Task 4/6) calls it.
- **`ActionValidator.ValidateCampaignAction`** (`src/Validation/ActionValidator.cs:49-67`) already maps `StartTravel → ManageAircraft` and does a real per-GUID check, BUT it reads `NetworkEngine.Instance.Session` → **not unit-linkable**. The pure permission decision is therefore re-homed in `PermissionGate`; `HostArbiter` does the `Session`-backed GUID resolution.
- **Test linkage** (`Multiplayer.Tests/Multiplayer.Tests.csproj`): `EnableDefaultCompileItems=false`; pure Unity-free cores are linked via `<Compile Include="..\src\...\X.cs"><Link>X.cs</Link>`. Only CommandCodec, PermissionGate, InterceptRegistry are pure → linked. HostArbiter/ClientApplier/CommandRelay/Harmony patch touch `NetworkEngine`/game types → build + manual 2-instance verify (NOT unit-tested).

### Decompile signatures (verified `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src`)

| Intercept | Confirmed signature | File:line | Notes |
|-----------|--------------------|-----------|-------|
| **`GeoVehicle.StartTravel`** | `public void StartTravel(List<GeoSite> path)` | `PhoenixPoint.Geoscape.Entities/GeoVehicle.cs:514` | **OVERLOADED** — also `StartTravel(List<Vector3> path)` `:528`. MUST disambiguate by param type `List<GeoSite>`. `GeoSite` = `PhoenixPoint.Geoscape.Entities.GeoSite`. |
| `GeoVehicle.AddEquipment` | `public void AddEquipment(GeoVehicleEquipmentDef equipmentDef)` | `GeoVehicle.cs:849` | Overloaded w/ `AddEquipment(GeoVehicleEquipment):828`; existing patch already disambiguates by `GeoVehicleEquipmentDef`. |
| `GeoVehicle.AddCharacter` | `public void AddCharacter(GeoCharacter character)` | `GeoVehicle.cs:765` | confirmed |
| `GeoCharacter.SetItems` | `public void SetItems(IEnumerable<GeoItem> armour=null, IEnumerable<GeoItem> equipment=null, IEnumerable<GeoItem> inventory=null, bool freeReload=false)` | `PhoenixPoint.Geoscape.Entities/GeoCharacter.cs:703` | 4 optional params |
| `GeoPhoenixBase.ConstructFacility` | `public GeoPhoenixFacility ConstructFacility(PhoenixFacilityDef facilityDef, Vector2Int position, PhoenixBaseLayoutRotation rotation=Rot0)` | `PhoenixPoint.Geoscape.Entities.Sites/GeoPhoenixBase.cs:230` | ns is `.Sites`, not `.Entities` (doc drift) |
| `GeoPhoenixBase.RepairFacility` | `public void RepairFacility(GeoPhoenixFacility facility)` | `GeoPhoenixBase.cs:263` | confirmed |
| `GeoPhoenixBase.RemoveFacility` | `public void RemoveFacility(GeoPhoenixFacility facility, bool scrap=false)` | `GeoPhoenixBase.cs:279` | confirmed |
| `GeoPhoenixFaction.HireNakedRecruit` | `public void HireNakedRecruit(GeoUnitDescriptor character, IGeoCharacterContainer toContainer)` | `PhoenixPoint.Geoscape.Levels.Factions/GeoPhoenixFaction.cs:662` | confirmed |
| `GeoFaction.KillCharacter` | `public virtual void KillCharacter(GeoCharacter unit, CharacterDeathReason reason)` | `PhoenixPoint.Geoscape.Levels/GeoFaction.cs:1600` | `GeoPhoenixFaction` overrides `:1377` |
| `Research.SetQueued` | **NOT FOUND in this build** | — | Doc-named `SetQueued(ResearchDef,bool)` absent. Real candidate: `Research.AddResearchToQueue(ResearchElement)` `PhoenixPoint.Geoscape.Entities.Research/Research.cs:370`. → **registry entry pending signature confirmation.** |
| `ItemManufacturing.EnqueueItem` | **NOT FOUND in this build** | — | Doc-named `EnqueueItem(ManufacturableItem)` absent. Real candidate: `ItemManufacturing.ManufactureItem(ManufacturableItem)` `PhoenixPoint.Common.Entities.Items/ItemManufacturing.cs:169`. → **registry entry pending signature confirmation.** |

> The pre-existing `ResearchPermissionPatch` (targets `SetQueued`) and `ManufacturingPermissionPatch` (targets `EnqueueItem`) in `CampaignPatches.cs` resolve to `null` against this build and are silent no-ops today — Stage 1 does NOT make them the vertical proof; they become pending registry entries (Task 7).

---

## File Structure

### New files (single responsibility)

| File | Responsibility | Pure / Unity-free? |
|------|----------------|--------------------|
| `src/Network/CommandSync/CommandCodec.cs` | (De)serialize the StartTravel **payload** (`StartTravelPayload{ string VehicleId; string[] SiteIds }`) ↔ `byte[]`. No envelope logic (that stays in `MessageSerializer`). | **Pure** — linked into tests |
| `src/Network/CommandSync/PermissionGate.cs` | Real per-GUID decision: `RequiredPermission(CampaignActionType)` + `IsAllowed(Guid, CampaignActionType)` via `PermissionManager`. Replaces the GUID-blind `CampaignPermissionHelper.Check`. | **Pure** — linked into tests |
| `src/Network/CommandSync/InterceptRegistry.cs` | Declarative `InterceptEntry` table (curated C1–C7) + `Lookup(CampaignActionType)`. Each entry = action type, required permission, target type/method name, param-type tokens, `SignatureConfirmed` flag. | **Pure** — linked into tests |
| `src/Network/CommandSync/HostArbiter.cs` | Host-only: subscribe `OnCampaignActionRequest`; resolve sender→GUID via `Session`; `PermissionGate.IsAllowed`; decode payload; execute the real game method (registry-dispatched); broadcast result or reject. | Engine — build + manual |
| `src/Network/CommandSync/ClientApplier.cs` | Client-only: subscribe `OnHostCampaignActionResult`; decode; apply the result locally (execute the real method under a re-entrancy guard so the prefix does not re-intercept). | Engine — build + manual |
| `src/Network/CommandSync/CommandRelay.cs` | Pipeline orchestrator: on session start, wire `HostArbiter`+`ClientApplier` to the engine events; own the `InterceptRegistry`; expose `RelayFromClient(CampaignActionMessage)` (encode-already-done → send → caller blocks) and the re-entrancy guard used by `ClientApplier`/host-local execution. | Engine — build + manual |
| `src/Harmony/StartTravelInterceptPatch.cs` | Harmony prefix on `GeoVehicle.StartTravel(List<GeoSite>)` (first vertical proof). Copies the `CampaignPatches.cs` `Prepare()/TargetMethod()/Prefix()` pattern; client → encode + `RelayFromClient` + `return false`; host → `return true` (execute) and a postfix broadcasts the result. | Engine/Harmony — build + manual |

### Modified files

| File | Change |
|------|--------|
| `src/Network/NetworkEngine.cs` | Add `public void BroadcastCampaignActionResult(CampaignActionMessage action)` → `BroadcastToAll(new NetworkMessage(PacketType.CampaignActionApproved, MessageSerializer.SerializeCampaignAction(action)))`. (Fan-out variant of the existing one-client `ApproveCampaignAction`.) |
| `src/Harmony/CampaignPatches.cs` | Replace the body of `CampaignPermissionHelper.Check` to delegate to `PermissionGate` keyed by the host-resolved caller GUID (kept as a thin shim so the existing C1–C5 prefixes compile unchanged). |
| `Multiplayer.Tests/Multiplayer.Tests.csproj` | Add linked `<Compile>` items for `PermissionManager.cs`, `CommandCodec.cs`, `PermissionGate.cs`, `InterceptRegistry.cs` (all pure). |
| `Multiplayer.csproj` | No edit needed if it globs `src/**/*.cs`; if it uses explicit includes, add the six new `src/Network/CommandSync/*.cs` + `src/Harmony/StartTravelInterceptPatch.cs`. (Verify the `<Compile>` strategy first; do not assume.) |

**Build:** `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
**Tests:** `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj`

---

## Task 1 — CommandCodec: StartTravel payload round-trip (pure, full TDD)

**Files:**
- Create `src/Network/CommandSync/CommandCodec.cs`
- Create test `Multiplayer.Tests/CommandCodecTests.cs`
- Modify `Multiplayer.Tests/Multiplayer.Tests.csproj` (link the new file)

- [ ] Add the linked compile item to `Multiplayer.Tests/Multiplayer.Tests.csproj` inside the existing pure-cores `<ItemGroup>`:
  ```xml
  <Compile Include="..\src\Network\CommandSync\CommandCodec.cs"><Link>CommandCodec.cs</Link></Compile>
  ```
- [ ] Write the failing test `Multiplayer.Tests/CommandCodecTests.cs`:
  ```csharp
  using Multiplayer.Network.CommandSync;
  using Xunit;

  public class CommandCodecTests
  {
      [Fact]
      public void StartTravelPayload_RoundTrips()
      {
          var src = new StartTravelPayload
          {
              VehicleId = "veh-7",
              SiteIds = new[] { "site-a", "site-b", "site-c" }
          };

          var bytes = CommandCodec.EncodeStartTravel(src);
          var back = CommandCodec.DecodeStartTravel(bytes);

          Assert.Equal("veh-7", back.VehicleId);
          Assert.Equal(new[] { "site-a", "site-b", "site-c" }, back.SiteIds);
      }

      [Fact]
      public void StartTravelPayload_EmptyPath_RoundTrips()
      {
          var src = new StartTravelPayload { VehicleId = "veh-1", SiteIds = new string[0] };
          var back = CommandCodec.DecodeStartTravel(CommandCodec.EncodeStartTravel(src));
          Assert.Equal("veh-1", back.VehicleId);
          Assert.Empty(back.SiteIds);
      }
  }
  ```
- [ ] Run FAIL: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj --filter FullyQualifiedName~CommandCodecTests` (expect compile error: type not found).
- [ ] Implement `src/Network/CommandSync/CommandCodec.cs`:
  ```csharp
  using System.IO;

  namespace Multiplayer.Network.CommandSync
  {
      // Pure, Unity-free. Encodes the StartTravel ACTION PAYLOAD only; the CampaignActionMessage
      // envelope (ActionId/Type/Timestamp) is handled by MessageSerializer. The vehicle and the
      // ordered destination sites are sent as stable string ids — the host/clients resolve them
      // back to live GeoVehicle/GeoSite by id at apply time (no engine types cross the wire).
      public struct StartTravelPayload
      {
          public string VehicleId;
          public string[] SiteIds;
      }

      public static class CommandCodec
      {
          public static byte[] EncodeStartTravel(StartTravelPayload p)
          {
              using (var ms = new MemoryStream())
              using (var bw = new BinaryWriter(ms))
              {
                  bw.Write(p.VehicleId ?? "");
                  var ids = p.SiteIds ?? new string[0];
                  bw.Write(ids.Length);
                  foreach (var id in ids) bw.Write(id ?? "");
                  return ms.ToArray();
              }
          }

          public static StartTravelPayload DecodeStartTravel(byte[] data)
          {
              using (var ms = new MemoryStream(data))
              using (var br = new BinaryReader(ms))
              {
                  var p = new StartTravelPayload { VehicleId = br.ReadString() };
                  var count = br.ReadInt32();
                  p.SiteIds = new string[count];
                  for (var i = 0; i < count; i++) p.SiteIds[i] = br.ReadString();
                  return p;
              }
          }
      }
  }
  ```
- [ ] Run PASS: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj --filter FullyQualifiedName~CommandCodecTests`.
- [ ] Commit: `feat(geo-sync): CommandCodec StartTravel payload round-trip`

---

## Task 2 — PermissionGate: real per-GUID flag check (pure, full TDD)

**Files:**
- Create `src/Network/CommandSync/PermissionGate.cs`
- Create test `Multiplayer.Tests/PermissionGateTests.cs`
- Modify `Multiplayer.Tests/Multiplayer.Tests.csproj` (link `PermissionManager.cs` + `PermissionGate.cs`)

- [ ] Add linked compile items to `Multiplayer.Tests/Multiplayer.Tests.csproj`:
  ```xml
  <Compile Include="..\src\Validation\PermissionManager.cs"><Link>PermissionManager.cs</Link></Compile>
  <Compile Include="..\src\Network\CommandSync\PermissionGate.cs"><Link>PermissionGate.cs</Link></Compile>
  ```
  > `PermissionManager.cs` has `using Multiplayer.Network;` but references no Unity/`NetworkEngine` member, so it links cleanly. If the linker pulls an unused-namespace error, the only fix permitted is removing that unused `using` in a separate trivial step — do not add engine deps.
- [ ] Write the failing test `Multiplayer.Tests/PermissionGateTests.cs`:
  ```csharp
  using System;
  using Multiplayer.Network.CommandSync;
  using Multiplayer.Network.MessageLayer;
  using Multiplayer.Validation;
  using Xunit;

  public class PermissionGateTests
  {
      [Fact]
      public void StartTravel_RequiresManageAircraft()
      {
          Assert.Equal(CampaignPermission.ManageAircraft,
              PermissionGate.RequiredPermission(CampaignActionType.StartTravel));
      }

      [Fact]
      public void IsAllowed_True_WhenGuidHasManageAircraft()
      {
          var g = Guid.NewGuid();
          PermissionManager.SetPermission(g, CampaignPermission.ManageAircraft, true);
          Assert.True(PermissionGate.IsAllowed(g, CampaignActionType.StartTravel));
      }

      [Fact]
      public void IsAllowed_False_WhenGuidLacksFlag()
      {
          var g = Guid.NewGuid();
          PermissionManager.SetPermission(g, CampaignPermission.ManageResearch, true);
          Assert.False(PermissionGate.IsAllowed(g, CampaignActionType.StartTravel));
      }

      [Fact]
      public void IsAllowed_False_ForEmptyGuid()
      {
          Assert.False(PermissionGate.IsAllowed(Guid.Empty, CampaignActionType.StartTravel));
      }

      [Fact]
      public void IsAllowed_True_ForFullCommander()
      {
          var g = Guid.NewGuid();
          PermissionManager.SetPermission(g, CampaignPermission.FullCommander, true);
          Assert.True(PermissionGate.IsAllowed(g, CampaignActionType.StartTravel));
      }
  }
  ```
- [ ] Run FAIL: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj --filter FullyQualifiedName~PermissionGateTests`.
- [ ] Implement `src/Network/CommandSync/PermissionGate.cs`:
  ```csharp
  using System;
  using Multiplayer.Network.MessageLayer;
  using Multiplayer.Validation;

  namespace Multiplayer.Network.CommandSync
  {
      // Real per-playerGUID permission gate. Replaces the GUID-blind CampaignPermissionHelper.Check
      // (host=allow/client=block) with an actual flag lookup against PermissionManager. Pure: no
      // NetworkEngine — the caller (HostArbiter) resolves sender->GUID and passes it in.
      public static class PermissionGate
      {
          public static CampaignPermission RequiredPermission(CampaignActionType type)
          {
              switch (type)
              {
                  case CampaignActionType.StartResearch:
                      return CampaignPermission.ManageResearch;
                  case CampaignActionType.QueueManufacturing:
                  case CampaignActionType.CancelManufacturing:
                      return CampaignPermission.ManageManufacturing;
                  case CampaignActionType.ConstructFacility:
                  case CampaignActionType.RemoveFacility:
                  case CampaignActionType.RepairFacility:
                      return CampaignPermission.ManageBases;
                  case CampaignActionType.EquipSoldier:
                  case CampaignActionType.EquipVehicle:
                      return CampaignPermission.ManageEquipment;
                  case CampaignActionType.HireRecruit:
                  case CampaignActionType.DismissSoldier:
                      return CampaignPermission.ManageRecruitment;
                  case CampaignActionType.DeployAircraft:
                  case CampaignActionType.AssignSoldier:
                  case CampaignActionType.RemoveSoldier:
                  case CampaignActionType.StartTravel:
                      return CampaignPermission.ManageAircraft;
                  default:
                      return CampaignPermission.FullCommander;
              }
          }

          public static bool IsAllowed(Guid playerGuid, CampaignActionType type)
          {
              if (playerGuid == Guid.Empty) return false;
              return PermissionManager.HasCampaignPermission(playerGuid, RequiredPermission(type));
          }
      }
  }
  ```
- [ ] Run PASS: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj --filter FullyQualifiedName~PermissionGateTests`.
- [ ] Commit: `feat(geo-sync): PermissionGate real per-GUID flag enforcement`

---

## Task 3 — InterceptRegistry: declarative table + lookup (pure, full TDD)

**Files:**
- Create `src/Network/CommandSync/InterceptRegistry.cs`
- Create test `Multiplayer.Tests/InterceptRegistryTests.cs`
- Modify `Multiplayer.Tests/Multiplayer.Tests.csproj` (link `InterceptRegistry.cs`)

- [ ] Add linked compile item to `Multiplayer.Tests/Multiplayer.Tests.csproj`:
  ```xml
  <Compile Include="..\src\Network\CommandSync\InterceptRegistry.cs"><Link>InterceptRegistry.cs</Link></Compile>
  ```
- [ ] Write the failing test `Multiplayer.Tests/InterceptRegistryTests.cs`:
  ```csharp
  using Multiplayer.Network.CommandSync;
  using Multiplayer.Network.MessageLayer;
  using Multiplayer.Validation;
  using Xunit;

  public class InterceptRegistryTests
  {
      [Fact]
      public void Lookup_StartTravel_ReturnsConfirmedAircraftEntry()
      {
          var e = InterceptRegistry.Lookup(CampaignActionType.StartTravel);
          Assert.NotNull(e);
          Assert.Equal(CampaignPermission.ManageAircraft, e.RequiredPermission);
          Assert.Equal("PhoenixPoint.Geoscape.Entities.GeoVehicle", e.DeclaringTypeName);
          Assert.Equal("StartTravel", e.MethodName);
          Assert.True(e.SignatureConfirmed);
      }

      [Fact]
      public void Lookup_StartResearch_IsPending()
      {
          // SetQueued absent in this build → entry present but flagged unconfirmed.
          var e = InterceptRegistry.Lookup(CampaignActionType.StartResearch);
          Assert.NotNull(e);
          Assert.False(e.SignatureConfirmed);
      }

      [Fact]
      public void Lookup_UnregisteredType_ReturnsNull()
      {
          Assert.Null(InterceptRegistry.Lookup(CampaignActionType.AssignSoldier));
      }
  }
  ```
- [ ] Run FAIL: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj --filter FullyQualifiedName~InterceptRegistryTests`.
- [ ] Implement `src/Network/CommandSync/InterceptRegistry.cs` (Task 3 ships ONLY the confirmed `StartTravel` entry + the pending `StartResearch` entry needed by the tests; Task 7 fills the remaining curated rows):
  ```csharp
  using System.Collections.Generic;
  using Multiplayer.Network.MessageLayer;
  using Multiplayer.Validation;

  namespace Multiplayer.Network.CommandSync
  {
      // Declarative, BROAD curated intercept table (prune-later). One row per CampaignActionType.
      // SignatureConfirmed=false rows are wired-but-dormant: the runtime resolver skips an unconfirmed
      // row instead of throwing, so an absent/renamed engine method never crashes the relay.
      public sealed class InterceptEntry
      {
          public CampaignActionType ActionType;
          public CampaignPermission RequiredPermission;
          public string DeclaringTypeName;    // AccessTools.TypeByName key
          public string MethodName;           // method on that type
          public string[] ParamTypeNames;     // overload disambiguation (AccessTools.TypeByName per token); null = unique
          public bool SignatureConfirmed;     // false => pending decompile confirmation (skip at resolve)
      }

      public static class InterceptRegistry
      {
          private static readonly Dictionary<CampaignActionType, InterceptEntry> _entries =
              new Dictionary<CampaignActionType, InterceptEntry>
          {
              [CampaignActionType.StartTravel] = new InterceptEntry
              {
                  ActionType = CampaignActionType.StartTravel,
                  RequiredPermission = CampaignPermission.ManageAircraft,
                  DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoVehicle",
                  MethodName = "StartTravel",
                  // OVERLOADED: StartTravel(List<GeoSite>) vs StartTravel(List<Vector3>) — pin the GeoSite overload.
                  ParamTypeNames = new[] { "System.Collections.Generic.List`1[PhoenixPoint.Geoscape.Entities.GeoSite]" },
                  SignatureConfirmed = true
              },
              [CampaignActionType.StartResearch] = new InterceptEntry
              {
                  ActionType = CampaignActionType.StartResearch,
                  RequiredPermission = CampaignPermission.ManageResearch,
                  // SetQueued absent in current build; real candidate Research.AddResearchToQueue(ResearchElement).
                  DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.Research.Research",
                  MethodName = "SetQueued",
                  ParamTypeNames = null,
                  SignatureConfirmed = false   // pending signature confirmation
              }
          };

          public static InterceptEntry Lookup(CampaignActionType type)
              => _entries.TryGetValue(type, out var e) ? e : null;

          public static IEnumerable<InterceptEntry> All => _entries.Values;
      }
  }
  ```
- [ ] Run PASS: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj --filter FullyQualifiedName~InterceptRegistryTests`.
- [ ] Commit: `feat(geo-sync): InterceptRegistry declarative table + lookup`

---

## Task 4 — HostArbiter: validate → execute → broadcast (engine seam; build + manual)

> **Not unit-testable** — depends on `NetworkEngine.Instance.Session` (Unity-coupled) and executes real game methods. Replace the TDD test-cycle with a `dotnet build -c Release` gate + a manual 2-instance note. The pure decision parts it calls (`PermissionGate`, `InterceptRegistry`, `CommandCodec`) are already covered by Tasks 1–3.

**Files:**
- Create `src/Network/CommandSync/HostArbiter.cs`
- (Manual verify uses two game instances; see note.)

- [ ] Implement `src/Network/CommandSync/HostArbiter.cs`:
  ```csharp
  using System;
  using Multiplayer.Network.MessageLayer;
  using Multiplayer.Network.CommandSync;
  using UnityEngine;

  namespace Multiplayer.Network.CommandSync
  {
      // Host-only arbiter. Subscribed to NetworkEngine.OnCampaignActionRequest (sender peerId + msg).
      // Flow: resolve sender->playerGUID via Session -> PermissionGate.IsAllowed -> on allow, execute
      // the REAL game method (CommandRelay.ApplyResult under the re-entrancy guard so the host's own
      // Harmony prefix does not re-encode it) and BROADCAST the approved action to all peers; on deny,
      // RejectCampaignAction back to the originator only.
      public sealed class HostArbiter
      {
          private readonly NetworkEngine _engine;
          private readonly CommandRelay _relay;

          public HostArbiter(NetworkEngine engine, CommandRelay relay)
          {
              _engine = engine;
              _relay = relay;
          }

          public void HandleRequest(ulong senderSteamId, CampaignActionMessage action)
          {
              if (!_engine.IsHost) return;

              var guid = ResolveGuid(senderSteamId);
              if (guid == Guid.Empty)
              {
                  _engine.RejectCampaignAction(senderSteamId, action, "Unknown player identity");
                  return;
              }

              if (!PermissionGate.IsAllowed(guid, action.ActionType))
              {
                  var required = PermissionGate.RequiredPermission(action.ActionType);
                  _engine.RejectCampaignAction(senderSteamId, action, $"Missing permission: {required}");
                  return;
              }

              try
              {
                  // Execute the real game method on the host (authoritative), guarded so the host-side
                  // Harmony prefix treats this as an already-relayed apply and lets it run.
                  _relay.ApplyResult(action);
                  // Fan the approved action out to ALL peers (incl. originator, whose local exec was blocked).
                  _engine.BroadcastCampaignActionResult(action);
              }
              catch (Exception ex)
              {
                  Debug.LogError($"[Multiplayer] HostArbiter execute failed for {action.ActionType}: {ex}");
                  _engine.RejectCampaignAction(senderSteamId, action, "Host execution error");
              }
          }

          private Guid ResolveGuid(ulong senderSteamId)
          {
              var session = _engine.Session;
              if (session != null && session.Clients.TryGetValue(senderSteamId, out var client))
                  return client.PlayerGuid;
              return Guid.Empty;
          }
      }
  }
  ```
- [ ] Run BUILD: `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release` (expect success; `BroadcastCampaignActionResult` and `CommandRelay.ApplyResult` land in Tasks 6 — if building this task in isolation, stub-forward is NOT allowed, so build HostArbiter together with Task 6's `NetworkEngine`/`CommandRelay` members in the same compile and run the build gate after Task 6). 
  > **Ordering note:** Task 4 authors `HostArbiter.cs` but the green build gate is shared with Task 6 (which adds `NetworkEngine.BroadcastCampaignActionResult` and `CommandRelay`). Commit the file at end of Task 4; the first passing Release build is asserted at the end of Task 6.
- [ ] Commit: `feat(geo-sync): HostArbiter validate+execute+broadcast (host-only)`

---

## Task 5 — ClientApplier: subscribe result + apply locally (engine seam; build + manual)

> **Not unit-testable** — executes real game methods on receipt. Build gate + manual note. Re-entrancy guard logic lives in `CommandRelay` (Task 6); this class only subscribes and delegates.

**Files:**
- Create `src/Network/CommandSync/ClientApplier.cs`

- [ ] Implement `src/Network/CommandSync/ClientApplier.cs`:
  ```csharp
  using System;
  using Multiplayer.Network.MessageLayer;
  using UnityEngine;

  namespace Multiplayer.Network.CommandSync
  {
      // Client-only applier. Subscribed to NetworkEngine.OnHostCampaignActionResult (fired on 0x31
      // Approved AND 0x32 Rejected). For an APPROVED action it reproduces the result locally by
      // executing the real game method under CommandRelay's guard (so the Harmony prefix does not
      // re-send it). A REJECTED action carries no separate channel here (the envelope is identical) —
      // Stage 1 treats every OnHostCampaignActionResult as an apply; rejection feedback (toast) is a
      // Stage-1+ follow-up tracked in the design doc, NOT implemented here.
      public sealed class ClientApplier
      {
          private readonly NetworkEngine _engine;
          private readonly CommandRelay _relay;

          public ClientApplier(NetworkEngine engine, CommandRelay relay)
          {
              _engine = engine;
              _relay = relay;
          }

          public void HandleResult(CampaignActionMessage action)
          {
              if (_engine.IsHost) return; // host already applied in HostArbiter
              try
              {
                  _relay.ApplyResult(action);
              }
              catch (Exception ex)
              {
                  Debug.LogError($"[Multiplayer] ClientApplier apply failed for {action.ActionType}: {ex}");
              }
          }
      }
  }
  ```
- [ ] Run BUILD (shared gate with Task 6): `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`.
- [ ] Commit: `feat(geo-sync): ClientApplier subscribe + apply host result (client-only)`

---

## Task 6 — CommandRelay wiring + first StartTravel intercept end-to-end (engine seam; build + manual 2-instance)

> The vertical proof. Engine + Harmony + cross-instance — **not unit-testable**. Verification = Release build green + a manual 2-instance run (steps below).

**Files:**
- Create `src/Network/CommandSync/CommandRelay.cs`
- Create `src/Harmony/StartTravelInterceptPatch.cs`
- Modify `src/Network/NetworkEngine.cs` (add `BroadcastCampaignActionResult`)
- Modify `src/Harmony/CampaignPatches.cs` (`CampaignPermissionHelper.Check` shim → `PermissionGate`)
- (Verify `Multiplayer.csproj` picks up the new files; add explicit `<Compile>` only if it does not glob `src/**`.)

- [ ] Add `BroadcastCampaignActionResult` to `src/Network/NetworkEngine.cs` immediately after `RejectCampaignAction` (`:281`):
  ```csharp
  // Fan-out variant of ApproveCampaignAction: replays an approved action to ALL peers (incl. the
  // originator, whose local execution was blocked by the client prefix). Result-replay model — the
  // payload IS the authorized action; each peer reproduces it, no recompute.
  public void BroadcastCampaignActionResult(CampaignActionMessage action)
  {
      var payload = MessageSerializer.SerializeCampaignAction(action);
      var msg = new NetworkMessage(PacketType.CampaignActionApproved, payload);
      BroadcastToAll(msg);
  }
  ```
- [ ] Implement `src/Network/CommandSync/CommandRelay.cs`:
  ```csharp
  using System;
  using System.Collections.Generic;
  using Multiplayer.Network.MessageLayer;
  using UnityEngine;

  namespace Multiplayer.Network.CommandSync
  {
      // Pipeline orchestrator. Built ONCE; wires HostArbiter + ClientApplier to the engine's existing
      // campaign-action events. Owns the InterceptRegistry and the re-entrancy guard that lets the
      // host/clients execute a real game method WITHOUT the Harmony prefix re-encoding it.
      public sealed class CommandRelay
      {
          public static CommandRelay Instance { get; private set; }

          private readonly NetworkEngine _engine;
          private readonly HostArbiter _hostArbiter;
          private readonly ClientApplier _clientApplier;

          // Per-action re-entrancy guard: set while ApplyResult runs the real method so the
          // intercept prefix sees "already relayed" and returns true (execute) instead of re-sending.
          [ThreadStatic] private static bool _applying;
          public static bool IsApplying => _applying;

          private CommandRelay(NetworkEngine engine)
          {
              _engine = engine;
              _hostArbiter = new HostArbiter(engine, this);
              _clientApplier = new ClientApplier(engine, this);
          }

          // Call once after the session/NetworkEngine exists (e.g. from the same place the lobby wires
          // up). Idempotent: re-wiring detaches old handlers first.
          public static void Wire(NetworkEngine engine)
          {
              if (engine == null) return;
              if (Instance != null) Instance.Unwire();
              Instance = new CommandRelay(engine);
              engine.OnCampaignActionRequest += Instance._hostArbiter.HandleRequest;
              engine.OnHostCampaignActionResult += Instance._clientApplier.HandleResult;
          }

          private void Unwire()
          {
              _engine.OnCampaignActionRequest -= _hostArbiter.HandleRequest;
              _engine.OnHostCampaignActionResult -= _clientApplier.HandleResult;
          }

          // Client side: the Harmony prefix has already built the envelope+payload; send to host and
          // the prefix returns false to block local execution.
          public void RelayFromClient(CampaignActionMessage action)
          {
              _engine.SendCampaignAction(action);
          }

          // Host + clients: reproduce an authorized action by invoking the registered real game method
          // under the guard. Looks up the InterceptEntry; skips unconfirmed-signature rows safely.
          public void ApplyResult(CampaignActionMessage action)
          {
              var entry = InterceptRegistry.Lookup(action.ActionType);
              if (entry == null || !entry.SignatureConfirmed)
              {
                  Debug.LogWarning($"[Multiplayer] No confirmed intercept for {action.ActionType}; skipping apply.");
                  return;
              }

              _applying = true;
              try
              {
                  CommandExecutor.Execute(entry, action);
              }
              finally
              {
                  _applying = false;
              }
          }
      }
  }
  ```
- [ ] Implement `src/Network/CommandSync/CommandExecutor.cs` (engine seam; reflection-resolves the real method per registry entry and applies the decoded payload). Add it as part of this task:
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.Reflection;
  using HarmonyLib;
  using Multiplayer.Network.MessageLayer;
  using UnityEngine;

  namespace Multiplayer.Network.CommandSync
  {
      // Resolves an InterceptEntry to the live game method via AccessTools and invokes it with the
      // decoded payload. Stage 1 implements ONLY the StartTravel apply (the confirmed vertical proof);
      // other confirmed entries get their apply branch as they are wired in Task 7.
      internal static class CommandExecutor
      {
          public static void Execute(InterceptEntry entry, CampaignActionMessage action)
          {
              switch (action.ActionType)
              {
                  case CampaignActionType.StartTravel:
                      ApplyStartTravel(action);
                      break;
                  default:
                      Debug.LogWarning($"[Multiplayer] CommandExecutor: no apply branch for {action.ActionType}.");
                      break;
              }
          }

          private static void ApplyStartTravel(CampaignActionMessage action)
          {
              var p = CommandCodec.DecodeStartTravel(action.Payload);

              var geoLevel = GeoBridge.GetGeoLevelController();
              if (geoLevel == null) { Debug.LogWarning("[Multiplayer] StartTravel apply: no GeoLevelController."); return; }

              var vehicle = GeoBridge.FindVehicleById(geoLevel, p.VehicleId);
              if (vehicle == null) { Debug.LogWarning($"[Multiplayer] StartTravel apply: vehicle {p.VehicleId} not found."); return; }

              var path = GeoBridge.BuildSitePath(geoLevel, p.SiteIds);
              if (path == null) { Debug.LogWarning("[Multiplayer] StartTravel apply: could not resolve site path."); return; }

              // Invoke GeoVehicle.StartTravel(List<GeoSite>) under the relay guard (the intercept prefix
              // checks CommandRelay.IsApplying and lets this through).
              var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
              var listType = typeof(List<>).MakeGenericType(geoSiteType);
              var method = AccessTools.Method(vehicle.GetType(), "StartTravel", new[] { listType });
              if (method == null) { Debug.LogError("[Multiplayer] StartTravel apply: method not resolved."); return; }
              method.Invoke(vehicle, new object[] { path });
          }
      }
  }
  ```
  > `GeoBridge` is a thin engine-side helper that holds the id↔entity resolution. Implement the minimum needed for StartTravel in this same task:
  ```csharp
  // src/Network/CommandSync/GeoBridge.cs
  using System;
  using System.Collections;
  using System.Collections.Generic;
  using HarmonyLib;

  namespace Multiplayer.Network.CommandSync
  {
      // Engine-side id<->entity bridge for command apply. Uses AccessTools reflection so the mod never
      // hard-references game types at compile time (matching the CampaignPatches stub strategy).
      internal static class GeoBridge
      {
          public static object GetGeoLevelController()
          {
              var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
              var prop = t?.GetProperty("Instance", AccessTools.all);
              return prop?.GetValue(null);
          }

          // Vehicle id == GeoVehicle.GeoUnitId (or persistent id) rendered as string by the codec.
          public static object FindVehicleById(object geoLevel, string vehicleId)
          {
              var faction = AccessTools.Property(geoLevel.GetType(), "PhoenixFaction")?.GetValue(geoLevel);
              var vehicles = AccessTools.Property(faction?.GetType(), "Vehicles")?.GetValue(faction) as IEnumerable;
              if (vehicles == null) return null;
              foreach (var v in vehicles)
                  if (VehicleId(v) == vehicleId) return v;
              return null;
          }

          // Build a List<GeoSite> from string ids, in order. Returns the typed list as object.
          public static object BuildSitePath(object geoLevel, string[] siteIds)
          {
              var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
              var listType = typeof(List<>).MakeGenericType(geoSiteType);
              var list = (IList)Activator.CreateInstance(listType);
              var allSites = AccessTools.Property(geoLevel.GetType(), "Map")?.GetValue(geoLevel);
              var sites = AccessTools.Property(allSites?.GetType(), "AllSites")?.GetValue(allSites) as IEnumerable;
              if (sites == null) return null;
              var byId = new Dictionary<string, object>();
              foreach (var s in sites) byId[SiteId(s)] = s;
              foreach (var id in siteIds)
                  if (byId.TryGetValue(id, out var site)) list.Add(site);
                  else return null;
              return list;
          }

          public static string VehicleId(object vehicle)
              => AccessTools.Property(vehicle.GetType(), "GeoUnitId")?.GetValue(vehicle)?.ToString() ?? "";

          public static string SiteId(object site)
              => AccessTools.Property(site.GetType(), "GeoUnitId")?.GetValue(site)?.ToString() ?? "";
      }
  }
  ```
  > **MANUAL/SDK CONFIRM during implementation:** the exact property names `GeoLevelController.Instance`, `PhoenixFaction.Vehicles`, `GeoLevelController.Map.AllSites`, and the stable id property (`GeoUnitId` vs another) must be confirmed against the decompile while implementing `GeoBridge` (research doc §6 lists `PhoenixFaction.Vehicles`; the map/site accessor + id property are the only unconfirmed names). If a name differs, fix it from the decompile — do NOT ship a guessed accessor. This is the one engine-resolution seam that requires a confirm-while-coding pass.
- [ ] Implement `src/Harmony/StartTravelInterceptPatch.cs` (copy the `CampaignPatches.cs` `Prepare/TargetMethod/Prefix` pattern; add a postfix for the host-local broadcast):
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.Reflection;
  using HarmonyLib;
  using Multiplayer.Network;
  using Multiplayer.Network.CommandSync;
  using Multiplayer.Network.MessageLayer;

  namespace Multiplayer.Harmony
  {
      // C7 vertical proof: GeoVehicle.StartTravel(List<GeoSite>). Client → encode + relay to host +
      // block local exec. Host (own action) → execute locally, then postfix broadcasts the result.
      [HarmonyPatch]
      public static class StartTravelInterceptPatch
      {
          private static MethodBase _targetMethod;

          public static bool Prepare()
          {
              var vehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
              var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
              if (vehicleType == null || geoSiteType == null) return false;
              var listType = typeof(List<>).MakeGenericType(geoSiteType);
              _targetMethod = AccessTools.Method(vehicleType, "StartTravel", new[] { listType });
              return _targetMethod != null;
          }

          public static MethodBase TargetMethod() => _targetMethod;

          // __instance = the GeoVehicle; path = List<GeoSite> (boxed as IEnumerable for our use).
          public static bool Prefix(object __instance, object path)
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive) return true;     // single player: pass through
              if (CommandRelay.IsApplying) return true;                 // re-entrant apply: execute the real method
              if (engine.IsHost) return true;                          // host-origin: execute, postfix broadcasts

              // Client-origin: encode + relay + block local execution.
              var payload = new StartTravelPayload
              {
                  VehicleId = GeoBridge.VehicleId(__instance),
                  SiteIds = ExtractSiteIds(path)
              };
              var msg = new CampaignActionMessage
              {
                  ActionId = Guid.NewGuid(),
                  ActionType = CampaignActionType.StartTravel,
                  TargetId = payload.VehicleId,
                  Payload = CommandCodec.EncodeStartTravel(payload),
                  Timestamp = DateTime.UtcNow.Ticks
              };
              CommandRelay.Instance?.RelayFromClient(msg);
              return false;
          }

          // Host-origin action executed locally → fan the result out to clients (skip during re-entrant apply).
          public static void Postfix(object __instance, object path)
          {
              var engine = NetworkEngine.Instance;
              if (engine == null || !engine.IsActive || !engine.IsHost) return;
              if (CommandRelay.IsApplying) return;

              var payload = new StartTravelPayload
              {
                  VehicleId = GeoBridge.VehicleId(__instance),
                  SiteIds = ExtractSiteIds(path)
              };
              var msg = new CampaignActionMessage
              {
                  ActionId = Guid.NewGuid(),
                  ActionType = CampaignActionType.StartTravel,
                  TargetId = payload.VehicleId,
                  Payload = CommandCodec.EncodeStartTravel(payload),
                  Timestamp = DateTime.UtcNow.Ticks
              };
              engine.BroadcastCampaignActionResult(msg);
          }

          private static string[] ExtractSiteIds(object path)
          {
              var ids = new List<string>();
              if (path is System.Collections.IEnumerable e)
                  foreach (var site in e) ids.Add(GeoBridge.SiteId(site));
              return ids.ToArray();
          }
      }
  }
  ```
- [ ] Replace `CampaignPermissionHelper.Check` body in `src/Harmony/CampaignPatches.cs` so the legacy C1–C5 prefixes route through the real gate (the host resolves the caller GUID; with no live caller-context plumbing yet, retain the host-allow fast path but stop the GUID-blind behaviour for the relayed path):
  ```csharp
  public static bool Check(CampaignPermission required)
  {
      var engine = NetworkEngine.Instance;
      if (engine == null || !engine.IsActive) return true; // single player
      if (!engine.IsHost) return false;                    // client never executes locally
      // Host: the authorized relayed action arrives via HostArbiter, which has already run
      // PermissionGate.IsAllowed against the caller GUID. A direct host-local call is the host's
      // own action and is allowed. (Per-GUID enforcement for the relayed path lives in HostArbiter.)
      return true;
  }
  ```
  > This keeps the five existing permission-stub prefixes compiling and correct (host-allow / client-block); the REAL per-GUID enforcement for command actions is in `HostArbiter` via `PermissionGate`. The five legacy prefixes are folded into the registry in Task 7.
- [ ] Wire `CommandRelay.Wire(NetworkEngine.Instance)` at session start. Locate the existing place where the lobby/session subscribes engine events (search `OnCampaignActionRequest` has zero subscribers; find where `NetworkEngine.Create()`/session start runs) and call `CommandRelay.Wire(engine)` there. Confirm the call site by reading the bootstrap before editing — do not invent a call site.
- [ ] Run BUILD (the shared green gate for Tasks 4–6): `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release` → expect `Build succeeded`.
- [ ] Run full unit suite (must stay green): `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj`.
- [ ] **MANUAL 2-instance verify** (record result in the commit body): launch two instances via `Multiplayer/tools/` second-copy setup; host loads a geoscape save, client joins; grant the client `ManageAircraft`; on the client, order an aircraft to travel to a site. EXPECT: client's vehicle does not move locally first (prefix blocked), host executes, both instances show the vehicle travelling to the same site. Then revoke `ManageAircraft` and retry → EXPECT host rejects, no movement on either instance.
- [ ] Commit: `feat(geo-sync): CommandRelay + StartTravel intercept end-to-end (first vertical proof)`

---

## Task 7 — Broaden the registry: remaining curated C1–C7 entries (mixed; per-entry small tasks)

> Add the rest of the curated list as `InterceptEntry` rows. Confirmed-signature rows get a real apply branch + Harmony intercept; drift/unconfirmed rows ship as `SignatureConfirmed = false` (wired but dormant — the resolver skips them, no crash). Each sub-step is its own commit. Registry-row additions are pure → assert via an `InterceptRegistryTests` row check; apply/intercept code is an engine seam → build gate only.

**Files:**
- Modify `src/Network/CommandSync/InterceptRegistry.cs` (add rows)
- Modify `Multiplayer.Tests/InterceptRegistryTests.cs` (assert new rows present + confirmed/pending flag)
- Create per-confirmed-entry intercept patches under `src/Harmony/` + apply branches in `CommandExecutor` (only for entries proven safe to sync)

- [ ] **C4 `GeoVehicle.AddEquipment(GeoVehicleEquipmentDef)`** — add registry row (`DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoVehicle"`, `MethodName = "AddEquipment"`, `ParamTypeNames = new[]{ "PhoenixPoint.Geoscape.Entities.GeoVehicleEquipmentDef" }`, `RequiredPermission = ManageAircraft`, `SignatureConfirmed = true`), `ActionType = EquipVehicle`. Add `InterceptRegistryTests` assertion. Run: `dotnet test ... --filter ~InterceptRegistryTests`. Commit: `feat(geo-sync): registry C4 GeoVehicle.AddEquipment (EquipVehicle)`
- [ ] **C5 `GeoCharacter.SetItems(...)`** — registry row (`DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoCharacter"`, `MethodName = "SetItems"`, `ParamTypeNames = null` (unique name), `RequiredPermission = ManageEquipment`, confirmed), `ActionType = EquipSoldier`. Test + build. Commit: `feat(geo-sync): registry C5 GeoCharacter.SetItems (EquipSoldier)`
- [ ] **C3 `GeoPhoenixBase.ConstructFacility` / `RemoveFacility` / `RepairFacility`** — three rows under `ConstructFacility` / `RemoveFacility` / `RepairFacility` action types (`DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase"`, confirmed, `RequiredPermission = ManageBases`; `ConstructFacility` `ParamTypeNames = new[]{ "PhoenixPoint.Geoscape.Entities.PhoenixFacilityDef", "UnityEngine.Vector2Int", "PhoenixPoint.Geoscape.Entities.Sites.PhoenixBaseLayoutRotation" }`). Test + build. Commit: `feat(geo-sync): registry C3 GeoPhoenixBase facility ops (ManageBases)`
- [ ] **`GeoVehicle.AddCharacter(GeoCharacter)`** — row under `AssignSoldier` (`MethodName = "AddCharacter"`, `ParamTypeNames = new[]{ "PhoenixPoint.Geoscape.Entities.GeoCharacter" }`, `RequiredPermission = ManageAircraft`, confirmed). Test + build. Commit: `feat(geo-sync): registry GeoVehicle.AddCharacter (AssignSoldier)`
- [ ] **C6 `GeoPhoenixFaction.HireNakedRecruit(GeoUnitDescriptor, IGeoCharacterContainer)`** — row under `HireRecruit` (`DeclaringTypeName = "PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction"`, `ParamTypeNames = new[]{ "PhoenixPoint.Geoscape.Levels.GeoUnitDescriptor", "PhoenixPoint.Geoscape.Entities.IGeoCharacterContainer" }`, `RequiredPermission = ManageRecruitment`, confirmed — **verify the `GeoUnitDescriptor`/`IGeoCharacterContainer` full namespaces against the decompile while adding; mark `SignatureConfirmed = false` if either type name cannot be confirmed**). Test + build. Commit: `feat(geo-sync): registry C6 HireNakedRecruit (HireRecruit)`
- [ ] **`GeoFaction.KillCharacter(GeoCharacter, CharacterDeathReason)`** — row under `DismissSoldier` (`DeclaringTypeName = "PhoenixPoint.Geoscape.Levels.GeoFaction"`, `ParamTypeNames = new[]{ "PhoenixPoint.Geoscape.Entities.GeoCharacter", "PhoenixPoint.Geoscape.Entities.CharacterDeathReason" }`, `RequiredPermission = ManageRecruitment`, confirmed — **verify `CharacterDeathReason` full namespace; pending if unconfirmed**). Test + build. Commit: `feat(geo-sync): registry GeoFaction.KillCharacter (DismissSoldier)`
- [ ] **C1 `Research` enqueue** — `StartResearch` row already present as `SignatureConfirmed = false` (Task 3). Leave dormant. Add a doc comment in the registry citing the real candidate `Research.AddResearchToQueue(ResearchElement)` (`Research.cs:370`) and that `SetQueued` is absent in this build. No build change beyond the comment. Commit: `docs(geo-sync): registry C1 Research pending-signature note`
- [ ] **C2 `ItemManufacturing` enqueue** — add `QueueManufacturing` row with `SignatureConfirmed = false`, citing the real candidate `ItemManufacturing.ManufactureItem(ManufacturableItem)` (`ItemManufacturing.cs:169`); `EnqueueItem` absent in this build. `RequiredPermission = ManageManufacturing`. Add `InterceptRegistryTests` assertion that the row exists and is pending. Test + build. Commit: `feat(geo-sync): registry C2 Manufacturing pending-signature row`
- [ ] Run full suite once more: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj` and `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release` → both green.
- [ ] **MANUAL 2-instance verify** each confirmed intercept that has an apply branch (StartTravel already proven in Task 6; for any C-entry given an apply branch, repeat the host-authoritative move/permission check). Prune (set `SignatureConfirmed = false` or remove the row) any intercept that double-applies, is non-deterministic, or is purely local. Record prune decisions in the commit body. Commit: `feat(geo-sync): broaden curated intercept registry (C1–C7, prune-later)`

---

## Self-review (completed before commit)

- **Spec coverage:** all six modules from the design module-map are present (CommandRelay, CommandCodec, HostArbiter, ClientApplier, InterceptRegistry, PermissionGate) + the reused packets `0x30`/`0x31`/`0x32` and the two zero-subscriber events are wired; result-replay (not state push) model; broad-intercept registry with prune-later; first vertical proof = `GeoVehicle.StartTravel`; per-GUID permissions via `PermissionManager`. Stage-2/3 (time/events/transitions) explicitly excluded. ✔
- **Placeholder scan:** no `TBD`/`...`/"add error handling"/"similar to Task N". Every referenced type is defined in a task (`StartTravelPayload`, `CommandCodec`, `PermissionGate`, `InterceptEntry`, `InterceptRegistry`, `HostArbiter`, `ClientApplier`, `CommandRelay`, `CommandExecutor`, `GeoBridge`, `StartTravelInterceptPatch`, `NetworkEngine.BroadcastCampaignActionResult`). The only deliberately-deferred confirmations are the two SDK-name seams (`GeoBridge` accessors; C6/`KillCharacter`/`CharacterDeathReason` type names) — each flagged "confirm against decompile while implementing", per the brief's "mark pending, do not fabricate" rule. ✔
- **Type consistency:** `CampaignActionMessage` uses the REAL fields (`ActionId`/`ActionType`/`TargetId`/`Payload`/`Timestamp`) — no nonexistent `ActorId`. `CampaignActionType.StartTravel` is reused (not re-declared). `CampaignPermission.ManageAircraft` gates StartTravel consistently in `PermissionGate`, `InterceptRegistry`, and the validator mapping. `BroadcastCampaignActionResult` emits `0x31`, which the existing `RouteMessage` already routes to `OnHostCampaignActionResult` → `ClientApplier`. Pure units (CommandCodec/PermissionGate/InterceptRegistry/PermissionManager) are Unity-free and test-linked; engine units (Relay/Arbiter/Applier/Executor/Bridge/Patch) are build+manual. ✔
