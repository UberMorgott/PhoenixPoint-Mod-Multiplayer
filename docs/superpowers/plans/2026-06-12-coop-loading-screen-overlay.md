# Co-op Loading Screen Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **DONE — SHIPPED + in-game-confirmed over DirectIP (`4976474`).** As-built differs from this plan on three points; authoritative description = [00-current-state](../../research/00-current-state.md) §Co-op loading screen + [09-disconnect-reconnect](../../research/09-disconnect-reconnect.md) §Co-op Loading Overlay: (1) overlay shows ONLY OTHER players (self-row hidden); (2) simultaneous reveal = native-curtain-HOLD (Harmony prefix suppresses auto-`LiftCurtain`) at the still-visible native screen — NOT an early-dropped curtain under a from-code cover (the "Cover"/`DropCurtainInstant` approach in the verification notes below was superseded); (3) phase-2 bar = live native `ProgressFill.fillAmount` raw, no easing. `RosterProgress` cadence raised to ≈20 Hz (`SnapshotIntervalMs=50`).

**Goal:** Show every co-op player's two-phase load progress (savegame download, then native world-load) on a shared top-right overlay drawn over the vanilla loading screen, with all players entering gameplay together at the existing BEGIN barrier.

**Architecture:** Host assigns a stable `slotIndex` (byte, arrival order, host=0) echoed in PEER_LIST; ALL progress is keyed by `slotIndex`. Each client reports its own phase-1 (download) and phase-2 (native load) percent to the host via the existing `LoadProgress` packet; the host aggregates into a compact `RosterProgress` snapshot broadcast UNRELIABLE at ≤5 Hz, and clients merge it monotonic-max per (slot,phase). Done is event-driven via a RELIABLE `LoadComplete`; the host broadcasts snapshots until all slots are done. A mod-owned `ScreenSpaceOverlay` canvas (sortingOrder 7000) renders one bar+label+% per player over an early-dropped native curtain.

**Tech Stack:** C#, Harmony (HarmonyLib), Unity uGUI (`Canvas`/`CanvasScaler`/`Image`/`Text`), xUnit 2.9.2 on net472 (`Multipleer.Tests`, pure Unity-free cores linked via `<Compile Include="..\src\..."><Link>`).

---

## File Structure

**New files**

- `src\Network\SlotAllocator.cs` — pure-logic: host-side stable `slotIndex` assignment (host=0, clients in arrival order) + reconnect reuse by identity key. Unity-free; linked into tests.
- `src\Network\RosterProgressTracker.cs` — pure-logic: receiver-side state. Monotonic-max merge per (slot,phase), `LoadComplete` done-set, `AllDone(expectedSlots)` gate, snapshot accessor for the UI. Unity-free; linked into tests.
- `src\UI\LoadOverlayController.cs` — MonoBehaviour: owns the `ScreenSpaceOverlay` canvas (sortingOrder 7000), builds/refreshes one row per slot, drives phase-2 read+broadcast each `Update`, SHOW/HIDE.
- `src\Harmony\CurtainShowPatch.cs` — Harmony Postfix on `LevelSwitchCurtainController.OnLevelStateChanged` that SHOWs the overlay when `newState == Level.State.Loading`.
- `Multipleer.Tests\SlotAllocatorTests.cs` — xUnit tests for `SlotAllocator`.
- `Multipleer.Tests\RosterProgressTrackerTests.cs` — xUnit tests for `RosterProgressTracker` + the phase-2 percent conversion helper.
- `Multipleer.Tests\RosterProgressSerializerTests.cs` — xUnit roundtrip tests for the new serializer methods + PEER_LIST `SlotIndex`.

**Modified files**

- `src\Network\MessageLayer\PacketType.cs` — add `RosterProgress = 0x1D`, `LoadComplete = 0x1E`.
- `src\Network\MessageLayer\MessageSerializer.cs` — add `SlotIndex` to `PeerListEntry` + its ser/deser; add `SerializeRosterProgress`/`DeserializeRosterProgress`, `SerializeLoadComplete`/`DeserializeLoadComplete`; add `ProgressRow` struct.
- `src\Network\SessionManager.cs` — `ClientInfo.SlotIndex`; allocate slots on `AddClient`; emit `SlotIndex` from `BuildPeerList`; read this peer's own slot in `HandlePeerList`; expose `LocalSlotIndex` + `GetRosterSlots()`.
- `src\Network\NetworkEngine.cs` — add `BroadcastUnreliable(NetworkMessage)`; route `RosterProgress` + `LoadComplete` in `RouteMessage`.
- `src\Network\SaveTransferCoordinator.cs` — host aggregates per-slot rows + broadcasts `RosterProgress` (unreliable, ≤5 Hz) in `Update`; client reports phase-1 keyed by slot; `SendLoadComplete` (reliable) on local load done; `OnLoadComplete` host done-tracking; `OnRosterProgress` client merge into a shared `RosterProgressTracker`; expose the tracker.
- `src\UI\MultiplayerUI.cs` — create `LoadOverlayController` once (under `ModGO`); early `DropCurtainInstant` at `OnLobbyPlay`; HIDE the overlay when the BEGIN barrier releases.

---

## Conventions captured from source (do not re-derive)

- **Serializer style** (`MessageSerializer.cs`): `using (var ms = new MemoryStream()) using (var bw = new BinaryWriter(ms)) { ... return ms.ToArray(); }`. Primitives: `bw.Write(ulong/int/byte)`, `bw.Write(s ?? "")` for strings, `bw.Write(guid.ToByteArray())`; read with `br.ReadUInt64()/ReadInt32()/ReadByte()/ReadString()/new Guid(br.ReadBytes(16))`. Tuples returned from Deserialize.
- **PacketType** (`PacketType.cs`) is `byte` enum; `SessionBegin = 0x1C` is the last in the Session block. Next free = `0x1D`, `0x1E`.
- **PeerListEntry** props: `SteamId, PlayerGuid, Nickname, Permissions, Ready, IsHost`. `PlayerGuid` is the persistent identity (SteamId may be 0 on DirectIP) — use it as the slot identity key.
- **Transport** (`ITransport.cs`): `void Send(ulong, byte[], bool reliable = true)`, `void Broadcast(byte[], bool reliable = true)`. `NetworkEngine.BroadcastToAll` hardcodes reliable → a new `BroadcastUnreliable` wrapper is required for snapshots.
- **NetworkMessage** ctor: `new NetworkMessage(PacketType, byte[] payload)`. `msg.SenderSteamId` is the authoritative transport sender id; `msg.Payload` the bytes.
- **Engine accessors**: `_engine.IsHost`, `_engine.LocalSteamId`, `_engine.Session`, `_engine.BroadcastToAll/BroadcastExcept/SendToHost`. `Session.GetConnectedClients()` → `IEnumerable<ulong>`; `Session.BuildPeerList()`; `ClientIdentity.PlayerGuid` = local persistent guid.
- **Curtain** (`LevelSwitchCurtainController.cs`, decompiled): `public void DropCurtainInstant()` (`:66`, parameterless force-down); SHOW seam `public void OnLevelStateChanged(Level level, Level.State prevState, Level.State newState)` (`:46`, `→Loading` at `:48`); `public event Action OnCurtainLifted` (`:29`). Owned by PhoenixGame (`PhoenixGame.cs:129`). Resolve the type dynamically via `AccessTools.TypeByName` (mirror `FinishLevelBarrierPatch.Prepare`), since it is not referenced by the mod assembly.
- **Native progress**: `GameUtl.CurrentLevel()?.LoadingProgress?.Progress` (float 0..1, may be null at load end) — read each frame, null-guard.
- **Overlay canvas pattern** (`MultiplayerUI.EnsureBarCanvas :92-112`, `LobbyPanel :120-137`): `go.AddComponent<Canvas>()`, `renderMode = ScreenSpaceOverlay`, `sortingOrder = N`; `CanvasScaler` `ScaleWithScreenSize`, `referenceResolution = new Vector2(1920,1080)`, `ScreenMatchMode.MatchWidthOrHeight`, `matchWidthOrHeight = 0.5f`. Parent under the mod component's `transform` (which lives under `ModGO`, the mod's persistent root). Display-only overlay → NO `GraphicRaycaster`, set `overrideSorting = true`.
- **Harmony patch style** (`SaveLoadPatches.cs`): `[HarmonyPatch]` on a `static` class, dynamic `Prepare()`/`TargetMethod()` using `AccessTools.TypeByName(...)` + `AccessTools.Method(...)`, all patch bodies wrapped in `try/catch` that logs and degrades safely.
- **Tests** (`Multipleer.Tests.csproj`): xUnit `[Fact]`, `Assert.Equal/True/False`. Add each new pure core as `<Compile Include="..\src\...\X.cs"><Link>X.cs</Link></Compile>`. `MessageSerializer.cs` is ALREADY linked.
- **Build:** `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`. **Tests:** `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj`. **Deploy (manual in-game):** `pwsh E:\DEV\PhoenixPoint\Multipleer\deploy.ps1`.

---

## Task 1 — slotIndex assignment + reconnect reuse (`SlotAllocator`, pure-logic TDD)

Implements spec "Identity — stable slotIndex": host=slot 0, clients in arrival order, reconnect reuses the slot keyed by identity (`PlayerGuid`).

**Files:**
- Create: `src\Network\SlotAllocator.cs`
- Create: `Multipleer.Tests\SlotAllocatorTests.cs`
- Modify: `Multipleer.Tests\Multipleer.Tests.csproj` (link the new core)

- [ ] Add the link line under the `<!-- Pure ... cores -->` group in `Multipleer.Tests\Multipleer.Tests.csproj` (after the `MessageSerializer.cs` line):
  ```xml
      <Compile Include="..\src\Network\SlotAllocator.cs"><Link>SlotAllocator.cs</Link></Compile>
  ```
- [ ] Write failing test file `Multipleer.Tests\SlotAllocatorTests.cs`:
  ```csharp
  using System;
  using Multipleer.Network;
  using Xunit;

  namespace Multipleer.Tests
  {
      public class SlotAllocatorTests
      {
          private static readonly Guid Host = Guid.NewGuid();
          private static readonly Guid A = Guid.NewGuid();
          private static readonly Guid B = Guid.NewGuid();

          [Fact]
          public void Host_Is_Slot_Zero()
          {
              var alloc = new SlotAllocator(Host);
              Assert.Equal((byte)0, alloc.SlotFor(Host));
          }

          [Fact]
          public void Clients_Get_Arrival_Order_Slots()
          {
              var alloc = new SlotAllocator(Host);
              Assert.Equal((byte)1, alloc.Assign(A));
              Assert.Equal((byte)2, alloc.Assign(B));
          }

          [Fact]
          public void Reconnect_Reuses_Same_Slot()
          {
              var alloc = new SlotAllocator(Host);
              var first = alloc.Assign(A);
              alloc.Assign(B);
              var again = alloc.Assign(A); // A reconnects
              Assert.Equal(first, again);
          }

          [Fact]
          public void SlotFor_Unknown_Throws_Or_Assigns_Via_Assign_Only()
          {
              var alloc = new SlotAllocator(Host);
              Assert.False(alloc.TryGetSlot(A, out _));
              alloc.Assign(A);
              Assert.True(alloc.TryGetSlot(A, out var s));
              Assert.Equal((byte)1, s);
          }
      }
  }
  ```
- [ ] Run (expected FAIL — `SlotAllocator` does not exist):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~SlotAllocatorTests`
- [ ] Create `src\Network\SlotAllocator.cs` (minimal impl):
  ```csharp
  using System;
  using System.Collections.Generic;

  namespace Multipleer.Network
  {
      /// <summary>
      /// Host-side stable slotIndex allocation. Host is always slot 0. Clients are assigned
      /// the next free slot in arrival order; a reconnecting identity (matched by PlayerGuid)
      /// reuses its original slot. Pure logic — no UnityEngine / transport dependency.
      /// </summary>
      public sealed class SlotAllocator
      {
          private readonly Dictionary<Guid, byte> _slots = new Dictionary<Guid, byte>();
          private byte _next = 1; // 0 reserved for host

          public SlotAllocator(Guid hostIdentity)
          {
              _slots[hostIdentity] = 0;
          }

          /// <summary>Assign (or reuse) the slot for an identity; returns its slotIndex.</summary>
          public byte Assign(Guid identity)
          {
              if (_slots.TryGetValue(identity, out var existing)) return existing;
              var slot = _next++;
              _slots[identity] = slot;
              return slot;
          }

          public byte SlotFor(Guid identity) => _slots[identity];

          public bool TryGetSlot(Guid identity, out byte slot) => _slots.TryGetValue(identity, out slot);
      }
  }
  ```
- [ ] Run (expected PASS):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~SlotAllocatorTests`
- [ ] Commit:
  `git add src/Network/SlotAllocator.cs Multipleer.Tests/SlotAllocatorTests.cs Multipleer.Tests/Multipleer.Tests.csproj`
  `git commit -m "feat(coop-load): SlotAllocator host-assigned stable slotIndex + reconnect reuse"`

---

## Task 2 — PEER_LIST slotIndex echo (serializer TDD)

Implements spec "Mapping echoed in PEER_LIST (slotIndex → displayName + steamId|0)". Adds `SlotIndex` to `PeerListEntry` and threads it through the existing PEER_LIST ser/deser. `MessageSerializer.cs` is already linked into the test project.

**Files:**
- Modify: `src\Network\MessageLayer\MessageSerializer.cs` (`PeerListEntry` class `:515-523`; `SerializePeerList :161-178`; `DeserializePeerList :180-201`)
- Create: `Multipleer.Tests\RosterProgressSerializerTests.cs`

- [ ] Write failing test `Multipleer.Tests\RosterProgressSerializerTests.cs` (PEER_LIST slot portion first):
  ```csharp
  using System;
  using System.Collections.Generic;
  using Multipleer.Network.MessageLayer;
  using Xunit;

  namespace Multipleer.Tests
  {
      public class RosterProgressSerializerTests
      {
          [Fact]
          public void PeerList_Roundtrips_SlotIndex()
          {
              var peers = new List<PeerListEntry>
              {
                  new PeerListEntry { SteamId = 0, PlayerGuid = Guid.NewGuid(), Nickname = "Host",
                                      Permissions = 0, Ready = true, IsHost = true, SlotIndex = 0 },
                  new PeerListEntry { SteamId = 77, PlayerGuid = Guid.NewGuid(), Nickname = "Bob",
                                      Permissions = 3, Ready = false, IsHost = false, SlotIndex = 1 },
              };
              var back = MessageSerializer.DeserializePeerList(MessageSerializer.SerializePeerList(peers));
              Assert.Equal((byte)0, back[0].SlotIndex);
              Assert.Equal((byte)1, back[1].SlotIndex);
              Assert.Equal("Bob", back[1].Nickname);
              Assert.True(back[0].IsHost);
          }
      }
  }
  ```
- [ ] Run (expected FAIL — `PeerListEntry.SlotIndex` does not exist; will not compile):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressSerializerTests`
- [ ] Add `SlotIndex` to `PeerListEntry` (`MessageSerializer.cs`), after the `IsHost` property:
  ```csharp
          public byte SlotIndex { get; set; }   // host-assigned stable slot; 0 = host
  ```
- [ ] In `SerializePeerList`, append after `bw.Write((byte)(peer.IsHost ? 1 : 0));`:
  ```csharp
                  bw.Write(peer.SlotIndex);
  ```
- [ ] In `DeserializePeerList`, add `SlotIndex` to the object initializer after `IsHost = br.ReadByte() != 0`:
  ```csharp
                      ,SlotIndex = br.ReadByte()
  ```
  (i.e. the initializer becomes `... IsHost = br.ReadByte() != 0, SlotIndex = br.ReadByte()`)
- [ ] Run (expected PASS):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressSerializerTests`
- [ ] Commit:
  `git add src/Network/MessageLayer/MessageSerializer.cs Multipleer.Tests/RosterProgressSerializerTests.cs`
  `git commit -m "feat(coop-load): echo slotIndex in PEER_LIST (PeerListEntry.SlotIndex)"`

---

## Task 3 — RosterProgress + LoadComplete packets (serializer TDD)

Implements spec "host-aggregated snapshot" wire format (`RosterProgress`: N × {slotIndex, phase, percent}) and "event-driven done" (`LoadComplete`). Adds the `ProgressRow` struct + four serializer methods + two `PacketType` values.

**Files:**
- Modify: `src\Network\MessageLayer\PacketType.cs` (`:29-30`, after `SessionBegin = 0x1C`)
- Modify: `src\Network\MessageLayer\MessageSerializer.cs` (add `ProgressRow` struct + methods)
- Modify: `Multipleer.Tests\RosterProgressSerializerTests.cs` (add cases)

- [ ] Add the failing tests to `RosterProgressSerializerTests.cs`:
  ```csharp
          [Fact]
          public void RosterProgress_Roundtrips_Rows()
          {
              var rows = new List<ProgressRow>
              {
                  new ProgressRow { SlotIndex = 0, Phase = 1, Percent = 100 },
                  new ProgressRow { SlotIndex = 1, Phase = 0, Percent = 42 },
                  new ProgressRow { SlotIndex = 2, Phase = 1, Percent = 7 },
              };
              var back = MessageSerializer.DeserializeRosterProgress(
                            MessageSerializer.SerializeRosterProgress(rows));
              Assert.Equal(3, back.Count);
              Assert.Equal((byte)1, back[0].Phase);
              Assert.Equal((byte)42, back[1].Percent);
              Assert.Equal((byte)2, back[2].SlotIndex);
          }

          [Fact]
          public void LoadComplete_Roundtrips()
          {
              var id = Guid.NewGuid();
              var (slot, transferId) = MessageSerializer.DeserializeLoadComplete(
                            MessageSerializer.SerializeLoadComplete(5, id));
              Assert.Equal((byte)5, slot);
              Assert.Equal(id, transferId);
          }
  ```
- [ ] Run (expected FAIL — `ProgressRow`, `SerializeRosterProgress`, `SerializeLoadComplete` do not exist):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressSerializerTests`
- [ ] In `PacketType.cs`, after `SessionBegin = 0x1C,` add:
  ```csharp
          RosterProgress = 0x1D,
          LoadComplete = 0x1E,
  ```
- [ ] In `MessageSerializer.cs`, add the `ProgressRow` struct beside `PeerListEntry` (top-level in the namespace):
  ```csharp
      /// <summary>One row of the host-aggregated RosterProgress snapshot (~3 bytes on the wire).</summary>
      public struct ProgressRow
      {
          public byte SlotIndex;
          public byte Phase;     // 0 = download, 1 = native load
          public byte Percent;   // 0..100
      }
  ```
- [ ] In `MessageSerializer.cs`, add the four methods (inside the `MessageSerializer` class, next to `SerializeLoadProgress`):
  ```csharp
          // ROSTER_PROGRESS (RosterProgress, 0x1D): host-aggregated per-slot snapshot, UNRELIABLE.
          public static byte[] SerializeRosterProgress(List<ProgressRow> rows)
          {
              using (var ms = new MemoryStream())
              using (var bw = new BinaryWriter(ms))
              {
                  bw.Write((byte)rows.Count);
                  foreach (var r in rows)
                  {
                      bw.Write(r.SlotIndex);
                      bw.Write(r.Phase);
                      bw.Write(r.Percent);
                  }
                  return ms.ToArray();
              }
          }

          public static List<ProgressRow> DeserializeRosterProgress(byte[] data)
          {
              using (var ms = new MemoryStream(data))
              using (var br = new BinaryReader(ms))
              {
                  var n = br.ReadByte();
                  var rows = new List<ProgressRow>(n);
                  for (var i = 0; i < n; i++)
                      rows.Add(new ProgressRow
                      {
                          SlotIndex = br.ReadByte(),
                          Phase = br.ReadByte(),
                          Percent = br.ReadByte()
                      });
                  return rows;
              }
          }

          // LOAD_COMPLETE (LoadComplete, 0x1E): a slot's load truly finished, RELIABLE.
          public static byte[] SerializeLoadComplete(byte slotIndex, Guid transferId)
          {
              using (var ms = new MemoryStream())
              using (var bw = new BinaryWriter(ms))
              {
                  bw.Write(slotIndex);
                  bw.Write(transferId.ToByteArray());
                  return ms.ToArray();
              }
          }

          public static (byte slotIndex, Guid transferId) DeserializeLoadComplete(byte[] data)
          {
              using (var ms = new MemoryStream(data))
              using (var br = new BinaryReader(ms))
              {
                  var slot = br.ReadByte();
                  var id = new Guid(br.ReadBytes(16));
                  return (slot, id);
              }
          }
  ```
- [ ] Run (expected PASS):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressSerializerTests`
- [ ] Commit:
  `git add src/Network/MessageLayer/PacketType.cs src/Network/MessageLayer/MessageSerializer.cs Multipleer.Tests/RosterProgressSerializerTests.cs`
  `git commit -m "feat(coop-load): RosterProgress + LoadComplete packets (ser/deser + PacketType)"`

---

## Task 4 — monotonic-max merge (`RosterProgressTracker`, pure-logic TDD)

Implements spec "Receiver merge: monotonic-max per (slot, phase)" — phase only advances, percent within a phase only increases; UDP reorder becomes invisible. The tracker is the shared receiver-side state for both the merge and (Task 5) done-tracking.

**Files:**
- Create: `src\Network\RosterProgressTracker.cs`
- Create: `Multipleer.Tests\RosterProgressTrackerTests.cs`
- Modify: `Multipleer.Tests\Multipleer.Tests.csproj` (link the new core)

- [ ] Add the link line in `Multipleer.Tests\Multipleer.Tests.csproj` (after the `SlotAllocator.cs` line):
  ```xml
      <Compile Include="..\src\Network\RosterProgressTracker.cs"><Link>RosterProgressTracker.cs</Link></Compile>
  ```
- [ ] Write failing test `Multipleer.Tests\RosterProgressTrackerTests.cs`:
  ```csharp
  using Multipleer.Network;
  using Xunit;

  namespace Multipleer.Tests
  {
      public class RosterProgressTrackerTests
      {
          [Fact]
          public void Percent_Only_Increases_Within_A_Phase()
          {
              var t = new RosterProgressTracker();
              t.Merge(slot: 1, phase: 0, percent: 40);
              t.Merge(slot: 1, phase: 0, percent: 10); // stale/reordered → ignored
              Assert.Equal((0, 40), t.Get(1));
          }

          [Fact]
          public void Phase_Only_Advances()
          {
              var t = new RosterProgressTracker();
              t.Merge(1, 1, 5);                  // already in load phase
              t.Merge(1, 0, 99);                 // late download packet → ignored
              Assert.Equal((1, 5), t.Get(1));
          }

          [Fact]
          public void Advancing_Phase_Resets_Percent_Baseline()
          {
              var t = new RosterProgressTracker();
              t.Merge(1, 0, 100);
              t.Merge(1, 1, 3);                  // new phase, lower percent is accepted
              Assert.Equal((1, 3), t.Get(1));
          }

          [Fact]
          public void Unknown_Slot_Reads_As_Phase0_Zero()
          {
              var t = new RosterProgressTracker();
              Assert.Equal((0, 0), t.Get(9));
          }
      }
  }
  ```
- [ ] Run (expected FAIL — `RosterProgressTracker` does not exist):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressTrackerTests`
- [ ] Create `src\Network\RosterProgressTracker.cs`:
  ```csharp
  using System.Collections.Generic;

  namespace Multipleer.Network
  {
      /// <summary>
      /// Receiver-side co-op load state. Merges RosterProgress rows monotonic-max per slot:
      /// phase only advances; percent only increases within a phase; advancing the phase resets
      /// the percent baseline. Also tracks event-driven done (LoadComplete) for the all-done gate.
      /// Pure logic — no UnityEngine dependency.
      /// </summary>
      public sealed class RosterProgressTracker
      {
          private readonly Dictionary<byte, (byte phase, byte percent)> _state
              = new Dictionary<byte, (byte, byte)>();
          private readonly HashSet<byte> _done = new HashSet<byte>();

          /// <summary>Merge one (slot, phase, percent) observation; returns true if state changed.</summary>
          public bool Merge(byte slot, byte phase, byte percent)
          {
              if (!_state.TryGetValue(slot, out var cur))
              {
                  _state[slot] = (phase, percent);
                  return true;
              }
              if (phase < cur.phase) return false;                       // stale phase
              if (phase == cur.phase && percent <= cur.percent) return false; // stale percent
              _state[slot] = (phase, percent);
              return true;
          }

          /// <summary>Current (phase, percent) for a slot; (0,0) if never seen.</summary>
          public (byte phase, byte percent) Get(byte slot)
              => _state.TryGetValue(slot, out var v) ? v : ((byte)0, (byte)0);

          public void MarkDone(byte slot) => _done.Add(slot);

          public bool IsDone(byte slot) => _done.Contains(slot);

          /// <summary>All expected slots have reported LoadComplete.</summary>
          public bool AllDone(IEnumerable<byte> expectedSlots)
          {
              foreach (var s in expectedSlots)
                  if (!_done.Contains(s)) return false;
              return true;
          }
      }
  }
  ```
- [ ] Run (expected PASS):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressTrackerTests`
- [ ] Commit:
  `git add src/Network/RosterProgressTracker.cs Multipleer.Tests/RosterProgressTrackerTests.cs Multipleer.Tests/Multipleer.Tests.csproj`
  `git commit -m "feat(coop-load): RosterProgressTracker monotonic-max merge per (slot,phase)"`

---

## Task 5 — done-tracking barrier (all-slots-done gate, pure-logic TDD)

Implements spec "Done — event-driven, never threshold": host keeps broadcasting until ALL slots are done. Extends `RosterProgressTracker` done-tracking with explicit tests for the gate (the methods exist from Task 4; this task verifies the all-done semantics in isolation, mirroring the spec's "done-tracking barrier" test).

**Files:**
- Modify: `Multipleer.Tests\RosterProgressTrackerTests.cs` (add gate tests)

- [ ] Add failing tests to `RosterProgressTrackerTests.cs`:
  ```csharp
          [Fact]
          public void AllDone_False_Until_Every_Expected_Slot_Reports()
          {
              var t = new RosterProgressTracker();
              var expected = new byte[] { 0, 1 };
              Assert.False(t.AllDone(expected));
              t.MarkDone(0);
              Assert.False(t.AllDone(expected));
              t.MarkDone(1);
              Assert.True(t.AllDone(expected));
          }

          [Fact]
          public void AllDone_Ignores_Extra_Done_Slots()
          {
              var t = new RosterProgressTracker();
              t.MarkDone(0);
              t.MarkDone(5);                         // slot that left / not expected
              Assert.True(t.AllDone(new byte[] { 0 }));
          }

          [Fact]
          public void MarkDone_Is_Idempotent()
          {
              var t = new RosterProgressTracker();
              t.MarkDone(0);
              t.MarkDone(0);
              Assert.True(t.IsDone(0));
              Assert.True(t.AllDone(new byte[] { 0 }));
          }
  ```
- [ ] Run (expected PASS immediately — methods exist from Task 4; this codifies the gate contract):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressTrackerTests`
  > Note: if any assertion FAILS, fix `AllDone`/`MarkDone`/`IsDone` in `RosterProgressTracker.cs` until green before committing.
- [ ] Commit:
  `git add Multipleer.Tests/RosterProgressTrackerTests.cs`
  `git commit -m "test(coop-load): all-slots-done gate contract for RosterProgressTracker"`

---

## Task 6 — wire SlotAllocator into SessionManager (build-passes + manual)

Implements spec slotIndex propagation: host allocates on join, stamps `BuildPeerList`, and every peer learns its own slot from the echoed roster. Engine seam (touches `SessionManager` which has UnityEngine-adjacent deps) → verified by build + 2-instance run, not unit-tested.

**Files:**
- Modify: `src\Network\SessionManager.cs` (`ClientInfo :576-586`; `AddClient :118-130`; `BuildPeerList :310-340`; `HandlePeerList :350-376`; add fields + accessors)

- [ ] Add a `SlotIndex` property to `ClientInfo` (after `LatencyMs`):
  ```csharp
          public byte SlotIndex { get; set; }           // host-assigned stable slot (echoed in PEER_LIST)
  ```
- [ ] In `SessionManager`, add fields near the other host state (a `SlotAllocator`, lazily built from the host identity, plus the local peer's own slot):
  ```csharp
          private SlotAllocator _slots;
          /// <summary>This peer's own host-assigned slot (host = 0; clients learn it from PEER_LIST).</summary>
          public byte LocalSlotIndex { get; private set; }
  ```
  Add `using` if needed (`SlotAllocator` is in the same `Multipleer.Network` namespace — no extra using).
- [ ] In `AddClient`, inside the `if (!_clients.ContainsKey(steamId))` block, assign a slot using the client's persistent identity. The host allocator is keyed by `PlayerGuid`; `AddClient` currently has only `steamId`+`endpoint`, so assign at PEER_LIST build time instead where `PlayerGuid` is known. Replace the slot wiring by initialising the allocator in `BuildPeerList` (next step) — leave `AddClient` unchanged except a comment:
  ```csharp
                  // SlotIndex is assigned in BuildPeerList (host), keyed by the client's persistent
                  // PlayerGuid so a reconnecting client reuses its slot.
  ```
- [ ] In `BuildPeerList`, initialise the allocator (host = slot 0 via `ClientIdentity.PlayerGuid`) and stamp each entry. Replace the host self-entry initializer to set `SlotIndex = 0`:
  ```csharp
              if (_slots == null) _slots = new SlotAllocator(ClientIdentity.PlayerGuid);

              peers.Add(new PeerListEntry
              {
                  SteamId = _engine.LocalSteamId,
                  PlayerGuid = ClientIdentity.PlayerGuid,
                  Nickname = HostNickname,
                  Permissions = 0,
                  Ready = HostReady,
                  IsHost = true,
                  SlotIndex = 0
              });
  ```
  And in the `foreach (var c in _clients.Values)` loop set `SlotIndex` from the allocator (assigning/reusing by `PlayerGuid`):
  ```csharp
                  c.SlotIndex = _slots.Assign(c.PlayerGuid);
                  peers.Add(new PeerListEntry
                  {
                      SteamId = c.SteamId,
                      PlayerGuid = c.PlayerGuid,
                      Nickname = c.PlayerName,
                      Permissions = c.Permissions,
                      Ready = c.IsReady,
                      IsHost = false,
                      SlotIndex = c.SlotIndex
                  });
  ```
  Also set the host's own `LocalSlotIndex = 0;` right after initialising `_slots`.
- [ ] In `HandlePeerList`, after caching `_clientRoster = peers;`, learn THIS peer's own slot by matching the local persistent identity, and mirror each client's slot:
  ```csharp
              foreach (var p in peers)
                  if (p.PlayerGuid == ClientIdentity.PlayerGuid)
                      LocalSlotIndex = p.SlotIndex;
  ```
  And inside the existing mirror loop, after `client.IsReady = p.Ready;` add:
  ```csharp
                  client.SlotIndex = p.SlotIndex;
  ```
- [ ] Add a roster-slots accessor for the host's done-gate (used in Task 8). After `GetConnectedClients`:
  ```csharp
          /// <summary>All slotIndexes currently in the roster (host slot 0 + every connected client).</summary>
          public IEnumerable<byte> GetRosterSlots()
          {
              yield return 0; // host
              foreach (var c in _clients.Values) yield return c.SlotIndex;
          }
  ```
- [ ] Build (expected PASS):
  `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
- [ ] Manual in-game verification note: deploy (`pwsh deploy.ps1`), host + 1 client (`tools\launch-coop-test.ps1`), open the lobby; confirm host log shows the client assigned slot 1 and the client's `LocalSlotIndex == 1` (add a temporary `Debug.Log` if needed, remove before final). No automated test — slot wiring runs through the live transport/roster.
- [ ] Commit:
  `git add src/Network/SessionManager.cs`
  `git commit -m "feat(coop-load): assign+echo slotIndex via SessionManager (SlotAllocator wired)"`

---

## Task 7 — phase-2 percent conversion helper (pure-logic TDD)

Implements spec "DRIVE phase-2 … converts 0..1→byte". The float→byte clamp/scale is pure and must be exact (the engine `Progress` can sit <1.0 and can be null) — unit-test it; the per-frame read + broadcast wiring lands in Task 8.

**Files:**
- Modify: `src\Network\RosterProgressTracker.cs` (add a static `ProgressByte` helper — keeps it in a Unity-free, already-linked core)
- Modify: `Multipleer.Tests\RosterProgressTrackerTests.cs` (add conversion tests)

- [ ] Add failing tests to `RosterProgressTrackerTests.cs`:
  ```csharp
          [Theory]
          [InlineData(0f, (byte)0)]
          [InlineData(0.5f, (byte)50)]
          [InlineData(1f, (byte)100)]
          [InlineData(0.999f, (byte)99)]   // floor, never rounds up to a premature 100
          [InlineData(-0.2f, (byte)0)]     // clamp low
          [InlineData(1.5f, (byte)100)]    // clamp high
          public void ProgressByte_Clamps_And_Floors(float progress, byte expected)
          {
              Assert.Equal(expected, RosterProgressTracker.ProgressByte(progress));
          }
  ```
- [ ] Run (expected FAIL — `ProgressByte` does not exist):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressTrackerTests`
- [ ] Add the static helper to `RosterProgressTracker` (no UnityEngine — use `System.Math`):
  ```csharp
          /// <summary>Convert a native 0..1 load progress to a clamped, floored 0..100 byte.</summary>
          public static byte ProgressByte(float progress)
          {
              if (progress <= 0f) return 0;
              if (progress >= 1f) return 100;
              return (byte)System.Math.Floor(progress * 100f);
          }
  ```
- [ ] Run (expected PASS):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj --filter FullyQualifiedName~RosterProgressTrackerTests`
- [ ] Commit:
  `git add src/Network/RosterProgressTracker.cs Multipleer.Tests/RosterProgressTrackerTests.cs`
  `git commit -m "feat(coop-load): ProgressByte clamp+floor helper (phase-2 0..1->byte)"`

---

## Task 8 — host aggregation + snapshot broadcast + LoadComplete (build-passes + manual)

Implements spec "Progress propagation" (host aggregates → `RosterProgress` snapshot, UNRELIABLE, ≤5 Hz), "Done — event-driven" (`LoadComplete` reliable, broadcast until all done), and the client-side merge into the shared tracker. Engine seam → build + 2-instance run.

**Files:**
- Modify: `src\Network\NetworkEngine.cs` (add `BroadcastUnreliable` after `BroadcastToAll :181-185`)
- Modify: `src\Network\SaveTransferCoordinator.cs` (fields; per-slot phase-1 keying; `Update` snapshot; `SendLoadComplete`/`OnLoadComplete`/`OnRosterProgress`; expose tracker + local slot)

- [ ] Add `BroadcastUnreliable` to `NetworkEngine` (mirrors `BroadcastToAll` but passes `reliable: false`):
  ```csharp
          public void BroadcastUnreliable(NetworkMessage msg)
          {
              var data = msg.Serialize();
              Transport?.Broadcast(data, reliable: false);
          }
  ```
- [ ] In `SaveTransferCoordinator`, add fields near `_peerDownloadPct`:
  ```csharp
          // ─── Co-op load overlay state ─────────────────────────────────────
          // Host aggregate: per-slot (phase, percent). Client/host shared receiver view.
          private readonly Dictionary<byte, (byte phase, byte percent)> _slotProgress
              = new Dictionary<byte, (byte, byte)>();
          private readonly RosterProgressTracker _tracker = new RosterProgressTracker();
          private long _lastSnapshotMs = -1;
          private const long SnapshotIntervalMs = 200; // ≤5 Hz
          private bool _loadCompleteSent;

          /// <summary>Shared receiver-side roster progress for the overlay UI.</summary>
          public RosterProgressTracker Tracker => _tracker;
  ```
- [ ] Extend `OnLoadProgress` so the host stores per-SLOT progress (both phases) and feeds the tracker. Replace its body with:
  ```csharp
          public void OnLoadProgress(NetworkMessage msg)
          {
              if (!_engine.IsHost) return;
              var (_, phase, percent) = MessageSerializer.DeserializeLoadProgress(msg.Payload);

              // Map the sender to its slot via the roster, then aggregate monotonic-max.
              if (_engine.Session.TryGetSlotForPeer(msg.SenderSteamId, out var slot))
              {
                  _slotProgress[slot] = (phase, percent);
                  _tracker.Merge(slot, phase, percent);
              }
          }
  ```
  > Requires a host lookup `TryGetSlotForPeer` on `SessionManager` — add it next.
- [ ] Add `TryGetSlotForPeer` to `SessionManager` (after `GetRosterSlots`):
  ```csharp
          /// <summary>Host: resolve a transport sender id to its slotIndex (host self = slot 0).</summary>
          public bool TryGetSlotForPeer(ulong peerId, out byte slot)
          {
              if (peerId == _engine.LocalSteamId) { slot = 0; return true; }
              if (_clients.TryGetValue(peerId, out var c)) { slot = c.SlotIndex; return true; }
              slot = 0;
              return false;
          }
  ```
- [ ] Make the client report its OWN phase-1 keyed by slot. `ReportDownloadProgress` already sends `SerializeLoadProgress(LocalSteamId, 0, pct)`; the host maps sender→slot in `OnLoadProgress`, so no payload change is needed. Add a phase-2 reporter the overlay will call (Task 10), placed next to `ReportDownloadProgress`:
  ```csharp
          /// <summary>Client/host: report this peer's phase-2 (native load) percent to the host.</summary>
          public void ReportLoadProgress(byte percent)
          {
              var payload = MessageSerializer.SerializeLoadProgress(_engine.LocalSteamId, 1, percent);
              if (_engine.IsHost)
              {
                  // Host has no host→host hop: aggregate its own slot 0 directly.
                  _slotProgress[0] = (1, percent);
                  _tracker.Merge(0, 1, percent);
              }
              else
              {
                  _engine.SendToHost(new NetworkMessage(PacketType.LoadProgress, payload));
              }
          }
  ```
- [ ] Add host snapshot broadcast to `Update` (after the existing barrier-timeout block, still host-only). Insert before the method's end:
  ```csharp
              // Broadcast the aggregated per-slot snapshot at ≤5 Hz while the barrier is open.
              var now = NowMs();
              if (_barrierOpen && now - _lastSnapshotMs >= SnapshotIntervalMs)
              {
                  _lastSnapshotMs = now;
                  var rows = new List<ProgressRow>(_slotProgress.Count);
                  foreach (var kv in _slotProgress)
                      rows.Add(new ProgressRow { SlotIndex = kv.Key, Phase = kv.Value.phase, Percent = kv.Value.percent });
                  var payload = MessageSerializer.SerializeRosterProgress(rows);
                  _engine.BroadcastUnreliable(new NetworkMessage(PacketType.RosterProgress, payload));
              }
  ```
  > Note: the early `return;` statements at the top of `Update` (`!_engine.IsHost || !_barrierOpen`) gate the WHOLE method — move this snapshot block ABOVE the timeout's `return`-guards is unnecessary because both require `_barrierOpen`; keep the existing guards and append this block (it re-checks `_barrierOpen`).
- [ ] Add `using System.Collections.Generic;` is already present in `SaveTransferCoordinator.cs` (HashSet/Dictionary used). Confirm `ProgressRow`/`RosterProgressTracker` resolve (same `Multipleer.Network` / `Multipleer.Network.MessageLayer` namespaces already imported).
- [ ] Add `LoadComplete` send + host done-tracking + client snapshot merge. Add these methods near the barrier section:
  ```csharp
          /// <summary>This peer's load is truly finished (event-driven done) — tell the host, reliably.</summary>
          public void SendLoadComplete()
          {
              if (_loadCompleteSent) return;
              _loadCompleteSent = true;
              var slot = _engine.Session.LocalSlotIndex;
              _tracker.MarkDone(slot); // local self-done
              if (_engine.IsHost) { TryReleaseBarrier(); return; }
              var payload = MessageSerializer.SerializeLoadComplete(slot, _rxTransferId);
              _engine.SendToHost(new NetworkMessage(PacketType.LoadComplete, payload));
          }

          /// <summary>Host: a client reported its load complete.</summary>
          public void OnLoadComplete(NetworkMessage msg)
          {
              if (!_engine.IsHost) return;
              var (slot, _) = MessageSerializer.DeserializeLoadComplete(msg.Payload);
              _tracker.MarkDone(slot);
              TryReleaseBarrier();
          }

          /// <summary>All peers: merge a host snapshot into the shared tracker for the overlay.</summary>
          public void OnRosterProgress(NetworkMessage msg)
          {
              var rows = MessageSerializer.DeserializeRosterProgress(msg.Payload);
              foreach (var r in rows) _tracker.Merge(r.SlotIndex, r.Phase, r.Percent);
          }
  ```
- [ ] Reset the new state in `OpenBarrier` (so a re-host is clean). Append to `OpenBarrier`:
  ```csharp
              _slotProgress.Clear();
              _lastSnapshotMs = -1;
              _loadCompleteSent = false;
  ```
- [ ] Build (expected PASS):
  `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
- [ ] Manual in-game verification note: after Task 9 routing lands, 2-instance run; host log should show snapshots broadcasting and both slots eventually `MarkDone` releasing the barrier. (Cannot unit-test: depends on live transport + roster + barrier.)
- [ ] Commit:
  `git add src/Network/NetworkEngine.cs src/Network/SaveTransferCoordinator.cs src/Network/SessionManager.cs`
  `git commit -m "feat(coop-load): host snapshot aggregation + LoadComplete done-tracking + client merge"`

---

## Task 9 — wire RouteMessage for new packets (build-passes)

Implements spec routing for `RosterProgress` + `LoadComplete`. Pure wiring → build only.

**Files:**
- Modify: `src\Network\NetworkEngine.cs` (`RouteMessage`, after `SessionBegin` case `:467-469`)

- [ ] In `RouteMessage`, after the `case PacketType.SessionBegin:` block, add:
  ```csharp
                  case PacketType.RosterProgress:
                      SaveTransfer?.OnRosterProgress(msg);
                      break;

                  case PacketType.LoadComplete:
                      SaveTransfer?.OnLoadComplete(msg);
                      break;
  ```
- [ ] Build (expected PASS):
  `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
- [ ] Manual note: routing has no isolated unit test; exercised by the 2-instance run in Tasks 8/10.
- [ ] Commit:
  `git add src/Network/NetworkEngine.cs`
  `git commit -m "feat(coop-load): route RosterProgress + LoadComplete in NetworkEngine"`

---

## Task 10 — overlay canvas + player rows (`LoadOverlayController`, build-passes + manual)

Implements spec "Screen + overlay" and "DRIVE phase-2". UI/engine seam (UnityEngine + live `CurrentLevel`) → build + manual; no unit test (the conversion logic it calls is already tested in Task 7).

**Files:**
- Create: `src\UI\LoadOverlayController.cs`
- Modify: `src\UI\MultiplayerUI.cs` (create the overlay once; expose Show/Hide entry — full wiring in Tasks 11/12)

- [ ] Create `src\UI\LoadOverlayController.cs`:
  ```csharp
  using System.Collections.Generic;
  using Base.Core;
  using Base.Levels;
  using Multipleer.Network;
  using UnityEngine;
  using UnityEngine.UI;

  namespace Multipleer.UI
  {
      /// <summary>
      /// Co-op loading overlay: a mod-owned ScreenSpaceOverlay canvas (sortingOrder 7000) drawn over
      /// the vanilla curtain. One row per slot (name + single bar + phase label + % text). Drives this
      /// peer's phase-2 native-load read and reports it to the host each frame (throttled).
      /// </summary>
      public sealed class LoadOverlayController : MonoBehaviour
      {
          private Canvas _canvas;
          private Transform _root;
          private bool _visible;

          private sealed class Row
          {
              public Text Name;
              public Image Fill;
              public Text Label;   // "Downloading" / "Loading"
              public Text Percent;
          }
          private readonly Dictionary<byte, Row> _rows = new Dictionary<byte, Row>();

          private int _lastReportedLoadPct = -1;

          private void EnsureCanvas()
          {
              if (_canvas != null) return;
              var go = new GameObject("MultipleerLoadOverlay");
              go.transform.SetParent(transform, false); // under ModGO (persistent root)

              _canvas = go.AddComponent<Canvas>();
              _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
              _canvas.overrideSorting = true;
              _canvas.sortingOrder = 7000; // above the native curtain (confirm in-game, open item #1)

              var scaler = go.AddComponent<CanvasScaler>();
              scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
              scaler.referenceResolution = new Vector2(1920, 1080);
              scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
              scaler.matchWidthOrHeight = 0.5f;
              // Display-only: no GraphicRaycaster.

              var panel = new GameObject("Panel");
              panel.transform.SetParent(go.transform, false);
              var prt = panel.AddComponent<RectTransform>();
              prt.anchorMin = new Vector2(1f, 1f); // top-right
              prt.anchorMax = new Vector2(1f, 1f);
              prt.pivot = new Vector2(1f, 1f);
              prt.anchoredPosition = new Vector2(-24f, -24f);
              prt.sizeDelta = new Vector2(460f, 270f); // ~quarter screen
              var vlg = panel.AddComponent<VerticalLayoutGroup>();
              vlg.spacing = 6f;
              vlg.childForceExpandWidth = true;
              vlg.childForceExpandHeight = false;
              var bg = panel.AddComponent<Image>();
              bg.color = new Color(0f, 0f, 0f, 0.55f);

              _root = panel.transform;
          }

          private Row BuildRow(byte slot, string name)
          {
              var go = new GameObject("Row" + slot);
              go.transform.SetParent(_root, false);
              go.AddComponent<RectTransform>().sizeDelta = new Vector2(0f, 44f);
              go.AddComponent<LayoutElement>().minHeight = 44f;

              var nameGo = new GameObject("Name");
              nameGo.transform.SetParent(go.transform, false);
              var nameTxt = nameGo.AddComponent<Text>();
              nameTxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
              nameTxt.fontSize = 18;
              nameTxt.color = Color.white;
              nameTxt.text = name;
              var nrt = nameTxt.rectTransform;
              nrt.anchorMin = new Vector2(0f, 0.5f); nrt.anchorMax = new Vector2(1f, 1f);
              nrt.offsetMin = new Vector2(6f, 0f); nrt.offsetMax = new Vector2(-6f, 0f);

              var barBg = new GameObject("BarBg");
              barBg.transform.SetParent(go.transform, false);
              var barBgImg = barBg.AddComponent<Image>();
              barBgImg.color = new Color(1f, 1f, 1f, 0.15f);
              var brt = barBgImg.rectTransform;
              brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(1f, 0.5f);
              brt.offsetMin = new Vector2(6f, 4f); brt.offsetMax = new Vector2(-6f, -2f);

              var fillGo = new GameObject("Fill");
              fillGo.transform.SetParent(barBg.transform, false);
              var fill = fillGo.AddComponent<Image>();
              fill.color = new Color(0.3f, 0.8f, 1f, 0.9f);
              fill.type = Image.Type.Filled;
              fill.fillMethod = Image.FillMethod.Horizontal;
              fill.fillOrigin = (int)Image.OriginHorizontal.Left;
              fill.fillAmount = 0f;
              var frt = fill.rectTransform;
              frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
              frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;

              var labelGo = new GameObject("Label");
              labelGo.transform.SetParent(barBg.transform, false);
              var label = labelGo.AddComponent<Text>();
              label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
              label.fontSize = 14;
              label.alignment = TextAnchor.MiddleLeft;
              label.color = Color.white;
              var lrt = label.rectTransform;
              lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
              lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = Vector2.zero;

              var pctGo = new GameObject("Pct");
              pctGo.transform.SetParent(barBg.transform, false);
              var pct = pctGo.AddComponent<Text>();
              pct.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
              pct.fontSize = 14;
              pct.alignment = TextAnchor.MiddleRight;
              pct.color = Color.white;
              var prt2 = pct.rectTransform;
              prt2.anchorMin = Vector2.zero; prt2.anchorMax = Vector2.one;
              prt2.offsetMin = Vector2.zero; prt2.offsetMax = new Vector2(-6f, 0f);

              return new Row { Name = nameTxt, Fill = fill, Label = label, Percent = pct };
          }

          public void Show()
          {
              EnsureCanvas();
              _canvas.gameObject.SetActive(true);
              _visible = true;
          }

          public void Hide()
          {
              if (_canvas != null) _canvas.gameObject.SetActive(false);
              _visible = false;
          }

          private void Update()
          {
              if (!_visible) return;
              var engine = NetworkEngine.Instance;
              if (engine == null || engine.SaveTransfer == null || engine.Session == null) return;

              // DRIVE phase-2: read native progress and report this peer's load percent (throttled).
              var level = GameUtl.CurrentLevel();
              var lp = level != null ? level.LoadingProgress : null;
              if (lp != null)
              {
                  var pct = RosterProgressTracker.ProgressByte(lp.Progress);
                  if (pct != _lastReportedLoadPct)
                  {
                      _lastReportedLoadPct = pct;
                      engine.SaveTransfer.ReportLoadProgress(pct);
                  }
              }

              Refresh(engine);
          }

          private void Refresh(NetworkEngine engine)
          {
              var tracker = engine.SaveTransfer.Tracker;
              foreach (var p in engine.Session.GetLobbyRoster())
              {
                  if (!_rows.TryGetValue(p.SlotIndex, out var row))
                  {
                      EnsureCanvas();
                      row = BuildRow(p.SlotIndex, p.Nickname);
                      _rows[p.SlotIndex] = row;
                  }
                  row.Name.text = p.Nickname;
                  var (phase, percent) = tracker.Get(p.SlotIndex);
                  row.Fill.fillAmount = percent / 100f;
                  row.Label.text = phase == 0 ? "Downloading" : "Loading";
                  row.Percent.text = percent + "%";
              }
          }
      }
  }
  ```
  > Requires `Session.GetLobbyRoster()` returning `List<PeerListEntry>` (host self + clients, with `SlotIndex`+`Nickname`). If it does not already exist, add it to `SessionManager` returning `BuildPeerList()` on the host and `_clientRoster` on a client.
- [ ] Confirm/add `GetLobbyRoster` in `SessionManager` (search first; the lobby UI likely already exposes the roster). If absent, add:
  ```csharp
          /// <summary>Full lobby roster (host self-entry + clients), with slot + nickname.</summary>
          public List<PeerListEntry> GetLobbyRoster()
              => _engine.IsHost ? BuildPeerList() : (_clientRoster ?? new List<PeerListEntry>());
  ```
- [ ] In `MultiplayerUI`, add a field + lazy creator for the overlay (next to `_barRoot`/`EnsureBarCanvas`):
  ```csharp
          private LoadOverlayController _loadOverlay;

          private LoadOverlayController EnsureLoadOverlay()
          {
              if (_loadOverlay == null)
                  _loadOverlay = gameObject.AddComponent<LoadOverlayController>();
              return _loadOverlay;
          }
  ```
- [ ] Build (expected PASS):
  `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
- [ ] Manual in-game verification note: overlay render + phase-2 read are engine seams — verify in a 2-instance run after Task 11 wires SHOW: confirm both player rows appear top-right, bars advance during download then reset+advance during load, % text matches the vanilla bar. Resolve open item #1 (does sortingOrder 7000 render above the native curtain). No automated test.
- [ ] Commit:
  `git add src/UI/LoadOverlayController.cs src/UI/MultiplayerUI.cs src/Network/SessionManager.cs`
  `git commit -m "feat(coop-load): LoadOverlayController overlay canvas + per-slot rows + phase-2 drive"`

---

## Task 11 — early curtain-drop at Play + SHOW patch (build-passes + manual)

Implements spec "On Play: drop the native curtain EARLY" + "SHOW: Postfix `LevelSwitchCurtainController.OnLevelStateChanged` on `newState==Loading`". Engine seam → build + manual (open item #2: verify no state desync, keep fallback).

**Files:**
- Create: `src\Harmony\CurtainShowPatch.cs`
- Modify: `src\UI\MultiplayerUI.cs` (`OnLobbyPlay :191-208` — early curtain drop + Show overlay)

- [ ] Create `src\Harmony\CurtainShowPatch.cs` (dynamic type resolution like `FinishLevelBarrierPatch`):
  ```csharp
  using System;
  using System.Reflection;
  using HarmonyLib;
  using Multipleer.Network;
  using Multipleer.UI;
  using UnityEngine;

  namespace Multipleer.Harmony
  {
      /// <summary>
      /// SHOW seam: after the native curtain drops for a level load (OnLevelStateChanged →
      /// newState == Level.State.Loading), bring up the co-op load overlay. Only acts during an
      /// active co-op session with a pending barrier. Type is resolved dynamically (the mod
      /// assembly does not reference LevelSwitchCurtainController).
      /// </summary>
      [HarmonyPatch]
      public static class CurtainShowPatch
      {
          private static Type _targetType;
          private static MethodBase _targetMethod;

          public static bool Prepare()
          {
              _targetType = AccessTools.TypeByName("Base.Utils.LevelSwitchCurtainController");
              if (_targetType == null) return false;
              _targetMethod = AccessTools.Method(_targetType, "OnLevelStateChanged");
              return _targetMethod != null;
          }

          public static MethodBase TargetMethod() => _targetMethod;

          // Signature: OnLevelStateChanged(Level level, Level.State prevState, Level.State newState).
          // newState is an enum; compare by its integer/string name to avoid a hard type ref.
          public static void Postfix(object newState)
          {
              try
              {
                  if (newState == null || newState.ToString() != "Loading") return;

                  var engine = NetworkEngine.Instance;
                  if (engine == null || !engine.IsActive) return;
                  var coord = engine.SaveTransfer;
                  if (coord == null || !coord.TransferActive) return;

                  MultiplayerUI.Instance?.ShowLoadOverlay();
              }
              catch (Exception e)
              {
                  Debug.LogError("[Multipleer] CurtainShowPatch failed: " + e.Message);
              }
          }
      }
  }
  ```
  > Requires `MultiplayerUI.Instance` (static) + `MultiplayerUI.ShowLoadOverlay()`. Add both next.
- [ ] In `MultiplayerUI`, add a static self-reference (set in its existing `Awake`/init; if none, add `private void Awake() { Instance = this; }`) and the show entry:
  ```csharp
          public static MultiplayerUI Instance { get; private set; }

          public void ShowLoadOverlay() => EnsureLoadOverlay().Show();
          public void HideLoadOverlay() => _loadOverlay?.Hide();
  ```
  > If `MultiplayerUI` has no `Awake`, add one that sets `Instance = this;` (does not disturb existing init, which runs from `MultipleerMain.OnModEnabled` via `AddComponent`).
- [ ] In `OnLobbyPlay`, drop the curtain EARLY and show the overlay before starting the transfer, with a fallback if the controller is unavailable. Replace the `if (_pendingChosenSave != null) { ... }` branch body's first line region:
  ```csharp
              if (_pendingChosenSave != null)
              {
                  DropCurtainEarly();           // phase-1 looks like one seamless vanilla load
                  ShowLoadOverlay();
                  engine.SaveTransfer?.HostStartSession(_pendingChosenSave);
                  return;
              }
  ```
  And in the picker callback, after `HostStartSession(chosen)` add the same two calls (`DropCurtainEarly(); ShowLoadOverlay();`) before it.
- [ ] Add `DropCurtainEarly` to `MultiplayerUI` (dynamic, with fallback per open item #2):
  ```csharp
          // Force the native curtain down early so phase-1 (download) shows under a vanilla-style
          // loading screen. Resolved dynamically; on any failure we fall back to the overlay's own
          // opaque backdrop (the overlay panel already paints a dark background).
          private void DropCurtainEarly()
          {
              try
              {
                  var t = HarmonyLib.AccessTools.TypeByName("Base.Utils.LevelSwitchCurtainController");
                  if (t == null) return;
                  var ctrl = UnityEngine.Object.FindObjectOfType(t);
                  if (ctrl == null) return;
                  var m = HarmonyLib.AccessTools.Method(t, "DropCurtainInstant", new System.Type[0]);
                  m?.Invoke(ctrl, null);
              }
              catch (System.Exception e)
              {
                  UnityEngine.Debug.LogWarning("[Multipleer] Early curtain drop failed (fallback to overlay backdrop): " + e.Message);
              }
          }
  ```
- [ ] Build (expected PASS):
  `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
- [ ] Manual in-game verification note (open item #2): 2-instance run; press Play; confirm the curtain drops immediately and the overlay appears over it without state desync (geoscape still enters correctly at BEGIN). If `DropCurtainInstant` misbehaves, the overlay's dark panel backdrop must still cover the screen — verify the fallback. No automated test.
- [ ] Commit:
  `git add src/Harmony/CurtainShowPatch.cs src/UI/MultiplayerUI.cs`
  `git commit -m "feat(coop-load): early DropCurtainInstant at Play + SHOW Postfix patch"`

---

## Task 12 — HIDE on barrier BEGIN (build-passes + manual)

Implements spec "HIDE … keep overlay up until the mod BEGIN barrier fires (all enter together)". The overlay hides when the BEGIN barrier releases this peer into the level. Engine seam → build + manual.

**Files:**
- Modify: `src\Network\SaveTransferCoordinator.cs` (`EnterLevel :423-441` — signal done + notify HIDE)
- Modify: `src\UI\MultiplayerUI.cs` (already has `HideLoadOverlay` from Task 11)

- [ ] In `SaveTransferCoordinator.EnterLevel`, after `_begun = true;` send this peer's `LoadComplete` (so the host's done-gate sees every slot) — but EnterLevel is the convergence AFTER the barrier already released, so instead signal local-done at the right point: this peer reports complete when its native load reaches the barrier, which the mod models as the LOADED/BEGIN handshake. Wire HIDE on BEGIN by hooking `EnterLevel`'s tail. Append to `EnterLevel`, after `_pendingResult = null;`:
  ```csharp
              // All peers enter together at BEGIN → the shared load is over; hide the overlay.
              Multipleer.UI.MultiplayerUI.Instance?.HideLoadOverlay();
  ```
- [ ] Ensure this peer's `LoadComplete` is actually emitted. The mod's existing `SendLoaded`/`OnClientLoaded` LOADED handshake already gates BEGIN; the co-op `LoadComplete` (Task 8) is the OVERLAY's done signal. Emit it where the local load truly finishes — when `LoadingProgress` goes null (load end, `Level.cs:148-149`) the overlay's `Update` can no longer read progress, so trigger it there. In `LoadOverlayController.Update`, replace the `if (lp != null) { ... }` else-path to fire once:
  ```csharp
              if (lp != null)
              {
                  var pct = RosterProgressTracker.ProgressByte(lp.Progress);
                  if (pct != _lastReportedLoadPct)
                  {
                      _lastReportedLoadPct = pct;
                      engine.SaveTransfer.ReportLoadProgress(pct);
                  }
              }
              else if (_lastReportedLoadPct >= 0)
              {
                  // Native load finished (LoadingProgress went null) → event-driven done.
                  _lastReportedLoadPct = -1;
                  engine.SaveTransfer.SendLoadComplete();
              }
  ```
  > `SendLoadComplete` is idempotent (`_loadCompleteSent` guard from Task 8), so repeated null frames send only once.
- [ ] Build (expected PASS):
  `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
- [ ] Run the FULL test suite (no regressions in the pure cores):
  `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj`
- [ ] Manual in-game verification note: 2-instance run end-to-end — both players see the overlay through download + load; when both finish, the host releases BEGIN and BOTH overlays hide as gameplay starts simultaneously. Confirm no overlay lingers and no peer enters early. No automated test (full barrier + scene transition).
- [ ] Commit:
  `git add src/Network/SaveTransferCoordinator.cs src/UI/LoadOverlayController.cs`
  `git commit -m "feat(coop-load): event-driven LoadComplete + HIDE overlay on BEGIN barrier"`

---

## Self-review

**Spec-coverage map:**
- Identity / stable slotIndex (host=0, arrival order, reconnect reuse, echoed in PEER_LIST, keyed everywhere) → T1 (allocator) + T2 (PEER_LIST echo) + T6 (wiring).
- Progress propagation — host-aggregated snapshot, selective, UNRELIABLE, ≤5 Hz, idempotent → T3 (wire format) + T8 (`Update` aggregation + `BroadcastUnreliable`).
- Receiver merge monotonic-max per (slot,phase) → T4.
- Done — event-driven `LoadComplete` reliable, broadcast until all done, never threshold → T3 (packet) + T5 (gate contract) + T8 (`SendLoadComplete`/`OnLoadComplete`/`TryReleaseBarrier`) + T12 (emit on `LoadingProgress` null).
- Screen + overlay — own ScreenSpaceOverlay canvas, `overrideSorting`, sortingOrder 7000, CanvasScaler 1920×1080, no raycaster, DontDestroyOnLoad root (ModGO), top-right ~quarter screen, one bar + phase label + % per player incl. host → T10.
- Hooks SHOW (Postfix `OnLevelStateChanged` on `Loading` + early force-drop) → T11; DRIVE phase-2 (`GameUtl.CurrentLevel()?.LoadingProgress?.Progress`, null-guard, 0..1→byte, throttle, `SerializeLoadProgress(slot, 1, pct)`) → T7 (conversion) + T8 (`ReportLoadProgress`) + T10 (per-frame read); HIDE on BEGIN barrier → T12.
- Routing for new packets → T9.
- Testing split (pure-logic TDD vs manual seams) → T1–T5,T7 are TDD; T6,T8–T12 are build+manual, each with an explicit manual-verification note. Matches spec §Testing exactly.
- Open items #1 (curtain sortingOrder) + #2 (early drop safety / fallback) → flagged in T10 and T11 manual notes; fallback backdrop implemented (overlay panel dark bg + `DropCurtainEarly` try/catch).
- Out-of-scope items (reconnect UX beyond slot reuse, permissions, smooth interpolation, tactical/geoscape concurrency) → not introduced. No gaps.

**Placeholder scan:** no "TBD"/"add error handling"/"similar to Task N" — every code block is concrete C#; cross-task dependencies are restated, not referenced.

**Type/name consistency (identical across tasks):** `SlotAllocator(Guid).Assign/SlotFor/TryGetSlot`; `PeerListEntry.SlotIndex` (byte); `ProgressRow{SlotIndex,Phase,Percent}` (all byte); `PacketType.RosterProgress=0x1D`,`LoadComplete=0x1E`; `MessageSerializer.SerializeRosterProgress/DeserializeRosterProgress/SerializeLoadComplete/DeserializeLoadComplete`; `RosterProgressTracker.Merge/Get/MarkDone/IsDone/AllDone/ProgressByte`; `ClientInfo.SlotIndex`, `SessionManager.LocalSlotIndex/GetRosterSlots/TryGetSlotForPeer/GetLobbyRoster`; `NetworkEngine.BroadcastUnreliable`; `SaveTransferCoordinator.Tracker/ReportLoadProgress/SendLoadComplete/OnLoadComplete/OnRosterProgress`; `MultiplayerUI.Instance/ShowLoadOverlay/HideLoadOverlay/EnsureLoadOverlay/DropCurtainEarly`; `LoadOverlayController.Show/Hide`. All consistent.
