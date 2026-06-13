# Replication Increment-2 — Entity Lifecycle (`0x36 GeoEntityOp`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. TDD, DRY, YAGNI, frequent commits — commit straight to inner `main` (NO branch, NO push); full test suite green before each commit.

**Goal:** Make host-created/destroyed geoscape ENTITIES exist on the client by replicating an authoritative entity create/destroy stream (`0x36 GeoEntityOp`), so a host-created Phoenix aircraft (VehicleID N) is present on the client and a subsequent host→client `StartTravel` for that vehicle resolves in `GeoBridge.FindVehicleById` instead of aborting at `CommandExecutor.cs:37` ("vehicle N not found").

**Architecture:** Host-authoritative entity lifecycle replication, extending the existing CommandSync transport exactly like the time-sync `0x34` packet and the `StartTravel` codec. A NEW reliable, ordered packet `0x36 GeoEntityOp` carries a small op `{OpType, defGuid, ownerFactionGuid, siteId, posX/Y/Z, vehicleId}`. (A) A pure, TDD-able `GeoEntityOpCodec` encodes/decodes it. (B) A client-only Harmony patch `HostEntityOpBroadcastPatch` postfixes the native birth/death seams — `GeoFaction.CreateVehicle`, `GeoFaction.CreateVehicleAtPosition`, `GeoFaction.UnregisterVehicle`, `GeoSite.DestroySite` — and (host-only) broadcasts the op, mirroring `StartTravelInterceptPatch.Postfix`. (C) A `ClientEntityOpApplier` applies each op under a new `[ThreadStatic] IsApplying` replication scope by running the NATIVE entity lifecycle: `VehicleCreated` → `GeoFaction.CreateVehicle(GeoSite, ComponentSetDef)` (which runs `Instantiate`→`DoEnterPlay`→`OnLevelStart`→`TeleportToSite`→fires `VehicleAdded`, so the marker comes from the lifecycle — per C18 we do NOT gate `VehicleAdded`), then reconciles `VehicleID`/`_lastVehicleIndex` to the host's authoritative value so the id is collision-free and `FindVehicleById` resolves; `VehicleRemoved` → `GeoVehicle.Destroy()`; `SiteRemoved` → `GeoSite.DestroySite()`. (D) An in-game 2-instance checkpoint confirms the host-created aircraft appears AND flies on the client.

**Tech Stack:** C# (net472), HarmonyLib (`AccessTools` dynamic type/method/field resolution + `TargetMethods()` multi-target single-postfix; the mod NEVER hard-references game types — all injected params typed `object`), xUnit 2.9.2 (`Multipleer.Tests`, pure codec TDD-first like `TimeStateCodecTests`), existing `NetworkEngine` (reliable `BroadcastToAll`, `RouteMessage`, `PacketType` enum), existing `MessageSerializer`/`CommandCodec` `BinaryWriter` layout conventions, existing `GeoBridge` id↔entity resolution, existing `CommandRelay.IsApplying` `[ThreadStatic]` guard pattern (a new sibling guard is added for replication apply).

---

## Grounding notes (decompile-verified — read before coding)

> Decompile root: `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src` (gitignored — verified via Bash grep + Read on real files). Mod source: `E:\DEV\PhoenixPoint\Multipleer\src`.

### Verified entity-lifecycle seams (file:line)

| Seam | file:line | Verified detail |
|------|-----------|-----------------|
| **Vehicle create (site)** | `GeoFaction.CreateVehicle(GeoSite site, ComponentSetDef vehicleDef)` `GeoFaction.cs:2011-2026` | `Instantiate<GeoVehicle>(vehicleDef)` `:2013` → `geoVehicle.VehicleID = ++_lastVehicleIndex` `:2015` → `Owner = this` `:2016` → `DoEnterPlay()` `:2020` → `OnLevelStart()` `:2021` → `TeleportToSite(site)` `:2022` → `VehicleAdded?.Invoke(this, geoVehicle)` `:2023`. The map marker is built by the lifecycle (`DoEnterPlay`/`Initialize`) BEFORE `VehicleAdded` — replay calls this whole method; do NOT gate `VehicleAdded` (C18). |
| **Vehicle create (pos)** | `GeoFaction.CreateVehicleAtPosition(Vector3 worldPos, ComponentSetDef vehicleDef)` `GeoFaction.cs:2028-2043` | Same shape, `SetOrientedGlobeWorldPosition(worldPos)` `:2038` instead of `TeleportToSite`, `++_lastVehicleIndex` `:2033`, `VehicleAdded?.Invoke` `:2041`. |
| **`_lastVehicleIndex`** | `private int _lastVehicleIndex;` `GeoFaction.cs:73` | Per-faction monotonic counter; bumped on create `:2015`/`:2033` and `TakeOverVehicle` `:2049`. NO engine uniqueness guard → forcing the host id needs explicit reconcile (clamp the counter ≥ assigned id). Serialized via `instanceData.LastVehicleIndex` `:376`/`:598`. |
| **`VehicleID`** | `public int VehicleID;` `GeoVehicle.cs:51` | Public int FIELD = stable per-faction vehicle id (the `GeoBridge.VehicleId` string key). |
| **Vehicle remove/destroy** | `GeoVehicle.Destroy()` `GeoVehicle.cs:599-610` | `MarkedForDestruction = true` `:606` → `OnVehicleDestroyed?.Invoke` `:607` → `OnExitPlay()` `:608` → `Object.Destroy(gameObject)` `:609`. `OnExitPlay` `:394-401` calls `Owner.UnregisterVehicle(this)` `:400`. |
| **`VehicleRemoved` event** | `GeoFaction.UnregisterVehicle(GeoVehicle)` `GeoFaction.cs:2084-2108`, fires `this.VehicleRemoved?.Invoke(this, vehicle)` `:2107` | Declared `public event FactionControllerVehicleEventHandler VehicleRemoved;` `GeoFaction.cs:247`. The host broadcast postfixes `UnregisterVehicle` (single choke for every removal path incl. `Destroy`). |
| **Vehicle `Owner`** | `public GeoFaction Owner { get; set; }` `GeoVehicle.cs:111-122` (backing `_owner`) | Owner faction; set inside `CreateVehicle` `:2016`. |
| **`GeoVehicle.Initialize`** | `GeoVehicle.cs:300-313` | Wires `Navigation.Arrived += OnArrived` `:309`, animator `:312`. Called by `DoEnterPlay` chain — replay runs it implicitly via `CreateVehicle`; we never call `Initialize` directly. |
| **Site remove/destroy** | `GeoSite.DestroySite()` `GeoSite.cs:849+` | Clean public removal: removes addons, completes/cancels active mission, then engine `UnregisterActor`→`SiteRemoved`. The client replay for `SiteRemoved`. |
| **`SiteRemoved`/`SiteAdded` events** | `GeoMap.SiteRemoved` `GeoMap.cs:291`, `GeoMap.SiteAdded` `GeoMap.cs:293` | Fired in `UnregisterSite` `:545` / `RegisterSite` `:497`, entered via `UnregisterActor` `:428-448` (`UnregisterSite`+`AllSites.Remove` `:437-438`) / `RegisterActor` `:402-426` (`AllSites.Add`+`RegisterSite` `:411-412`). |
| **Site identity** | `public int SiteId = -1;` `GeoSite.cs:45`; `public GeoFaction Owner` `:139`; `WorldPosition` (base `ActorComponent`) | `GeoSite.SiteId` is the int field (the `GeoBridge.SiteId(object)` string key); `GeoBridge.SiteId` the static string METHOD `GeoBridge.cs:77` is the codec helper, NOT the field. |
| **Site create primitive** | `GeoActorSpawner.SpawnSite(GeoSiteInstaceData siteInstance)` `GeoActorSpawner.cs:14` | Site creation requires a FULL `GeoSiteInstaceData` blob (mission/type/addons/seed) — that IS an InstanceData payload → **deferred to INC-3 `0x35`**. INC-2 does NOT replay `SiteCreated`. (See "Documented boundary" below.) |

### Def + faction resolution (wire ids)

- **Def id:** `BaseDef.Guid` (`public string Guid;` `BaseDef.cs:21`) is the stable cross-process def key. The vehicle def is `GeoVehicle.VehicleDef` (`GeoVehicleDef`, derives `ComponentSetDef`) via `this.Def<GeoVehicleDef>()` `GeoVehicle.cs:87`; its `.Guid` is the wire `DefGuid`.
- **Def resolve (client apply):** `DefRepository.GetDef(string guid)` returns `BaseDef` `DefRepository.cs:70`. Reach the repo via the same pattern `CreateVehicle` uses: `GameUtl.GameComponent<DefRepository>()` (resolved by reflection — see Task 4 code).
- **Owner faction id:** `GeoFaction.Def.Guid` (`GeoFaction.Def` `GeoFaction.cs:121` → `BaseDef.Guid`) is the wire `OwnerFactionGuid`. Client resolve: scan `GeoLevelController.Factions` (`public readonly IList<GeoFaction> Factions` `GeoLevelController.cs:85`) for `f.Def.Guid == ownerGuid`; common case is `PhoenixFaction` (manufactured aircraft).
- **Vehicle re-find (client, after apply):** existing `GeoBridge.FindVehicleById(geoLevel, vehicleId)` `GeoBridge.cs:39-47` scans `PhoenixFaction.Vehicles`. (INC-2 keeps the Phoenix-faction resolve already proven by StartTravel.)

### Existing transport conventions to mirror exactly

- **PacketType:** add `GeoEntityOp = 0x36` to the "Campaign Actions" block of `enum PacketType : byte` (`src/Network/MessageLayer/PacketType.cs:49`, right after `CampaignStateUpdate = 0x34`). `0x35` is reserved by the spec for `GeoStateDiff` (INC-3) — SKIP it, use `0x36`.
- **Reliable broadcast:** `NetworkEngine.BroadcastToAll(NetworkMessage)` `NetworkEngine.cs:185-189` (reliable; `BroadcastUnreliable` `:195` is the loss-tolerant variant — NOT used here, entity ops MUST be reliable). New send helper `BroadcastGeoEntityOp` mirrors `BroadcastTimingState` `NetworkEngine.cs:306-314` (build body via codec → wrap in `NetworkMessage(PacketType.GeoEntityOp, body)` → `BroadcastToAll`).
- **Route:** add a `case PacketType.GeoEntityOp:` to `RouteMessage` `NetworkEngine.cs:383-568` (alongside the `CampaignStateUpdate` case `:555-563`) that decodes via `GeoEntityOpCodec.Decode` and calls `ClientEntityOpApplier.Apply(op)`.
- **Codec layout:** `BinaryWriter`/`BinaryReader` over a `MemoryStream`, exactly like `CommandCodec.EncodeStartTravel` `CommandCodec.cs:33-44` (`bw.Write(string)`, `bw.Write(int)`, `bw.Write(float)`). Strings use the length-prefixed `BinaryWriter.Write(string)`; NEVER write a null string (`?? ""`).
- **Host-postfix broadcast pattern:** `StartTravelInterceptPatch.Postfix` `StartTravelInterceptPatch.cs:61-81` — gate `engine != null && engine.IsActive && engine.IsHost`, bail if `CommandRelay.IsApplying` (do not re-broadcast a relayed apply); here also bail if `EntityReplicationScope.IsApplying` (do not re-broadcast a client-side replay). Then build payload + `engine.BroadcastGeoEntityOp(op)`.
- **Replication apply scope:** new `EntityReplicationScope` mirrors `CommandRelay`'s `[System.ThreadStatic] private static bool _applying; public static bool IsApplying => _applying;` (`CommandRelay.cs:26-27`). The client applier sets it around the native lifecycle call so the very create/destroy postfixes that the replay triggers do NOT re-broadcast (host) and are recognized as replication (no recursion).
- **Auto-registration:** `MultipleerMain.OnModEnabled` does `harmony.PatchAll(...)` — new `[HarmonyPatch]` classes auto-discover; NO manual registration. New `src/` files need NO `Multipleer.csproj` edit (SDK globs `src/**/*.cs`).
- **Test linking:** `Multipleer.Tests/Multipleer.Tests.csproj` has `EnableDefaultCompileItems=false` `:7`; pure cores are linked individually (`CommandCodec.cs` link `:26`). A new pure file under unit test MUST get its own `<Compile Include="..\src\..."><Link>X.cs</Link></Compile>` line.

### `0x36 GeoEntityOp` payload layout (chosen)

One op per packet. Fixed, self-describing, forward-compatible (all 4 op-types enumerated; only Vehicle{Created,Removed}+SiteRemoved are produced in INC-2):

```
[OpType : byte]            # 1=VehicleCreated 2=VehicleRemoved 3=SiteCreated(reserved, INC-3) 4=SiteRemoved
[DefGuid : string]         # BaseDef.Guid of the vehicle def (VehicleCreated); "" otherwise
[OwnerFactionGuid : string]# GeoFaction.Def.Guid of the owner (VehicleCreated); "" otherwise
[SiteId : int]             # anchor GeoSite.SiteId (VehicleCreated via site) OR target site (SiteRemoved); -1 = none / use position
[PosX : float][PosY : float][PosZ : float]  # world position (VehicleCreated via position when SiteId == -1); else 0
[EntityId : int]           # authoritative VehicleID to assign (VehicleCreated/VehicleRemoved); unused for site ops
```

Rationale: a single flat record keeps the codec pure and trivially round-trippable; `SiteId` doubles as the create anchor (manufactured aircraft `TeleportToSite` the home base) and as the `SiteRemoved` target; `EntityId` carries the host's authoritative `VehicleID` so the client assigns the SAME id (collision-free per §9/C8). `Pos*` covers the rarer `CreateVehicleAtPosition` path (`SiteId == -1`).

### Documented boundaries (do NOT over-promise)

- **Arrival/`CurrentSite` authority on the client stays stale until INC-3.** INC-1's `ClientTravelEmitterSuppressPatch` suppresses the three `GeoVehicle` travel emitters (`set_Travelling`/`InitiateTravelling`/`OnArrived`) on the client. INC-2 makes the host-created vehicle EXIST and the host→client `StartTravel` RESOLVE, so the ship RENDERS and FLIES on the slaved clock via the whitelisted `NavigateRoutine`. But those travel emitters fire from inside the `NavigateRoutine` coroutine AFTER `EntityReplicationScope.IsApplying` has reset (the coroutine runs on later frames), so they are still suppressed — client-side landing/`CurrentSite`/site occupancy remain stale until the INC-3 `0x35 GeoStateDiff`. INC-2 acceptance is "appears AND flies", NOT "lands/occupies".
- **`SiteCreated` is deferred to INC-3.** A new host site (mission spawn, alien base) needs a full `GeoSiteInstaceData` blob (`GeoActorSpawner.SpawnSite`), which is exactly the InstanceData payload INC-3 ships. INC-2 wires the `SiteCreated` op-type into the codec for forward-compat but neither broadcasts nor applies it. `SiteRemoved` IS implemented (the destroy primitive `GeoSite.DestroySite()` needs only the site id).

---

## File Structure

### New files

| File | Responsibility | Pure / Unity-free? |
|------|----------------|--------------------|
| `src/Network/CommandSync/GeoEntityOpCodec.cs` | Pure: the `GeoEntityOp` struct + `GeoEntityOpType` enum + `Encode`/`Decode` (BinaryWriter layout above). No game types — plain primitives/strings. Single source of truth for the wire format. | **Pure → unit-tested** |
| `src/Network/CommandSync/EntityReplicationScope.cs` | Pure: `[ThreadStatic]` re-entrancy guard (`IsApplying`) + `using`-friendly enter/exit, mirroring `CommandRelay`'s guard. Lets the client run the native create/destroy lifecycle without its own birth/death postfixes re-broadcasting. | **Pure → unit-tested** |
| `src/Network/CommandSync/ClientEntityOpApplier.cs` | Client-only apply: decode-side dispatcher. `VehicleCreated` → resolve def+owner+site/pos via `GeoBridge`/reflection → `GeoFaction.CreateVehicle(...)` native lifecycle → reconcile `VehicleID`/`_lastVehicleIndex`. `VehicleRemoved` → find vehicle → `Destroy()`. `SiteRemoved` → find site → `DestroySite()`. All wrapped in `EntityReplicationScope`. | Engine/reflection — build + 2-instance |
| `src/Harmony/HostEntityOpBroadcastPatch.cs` | Client-only-import multi-target Harmony patch: `TargetMethods()` yields `GeoFaction.CreateVehicle`, `CreateVehicleAtPosition`, `UnregisterVehicle`, `GeoSite.DestroySite`. One `Postfix` (host-only gate) builds the matching `GeoEntityOp` and `engine.BroadcastGeoEntityOp(op)`. | Engine/Harmony — build + 2-instance |
| `Multipleer.Tests/GeoEntityOpCodecTests.cs` | xUnit: round-trip each op-type (all fields), op-type byte stability, null-string safety. | Test |
| `Multipleer.Tests/EntityReplicationScopeTests.cs` | xUnit: default false, true inside `using`, restored after dispose, nested restore. | Test |

### Modified files

| File | Change |
|------|--------|
| `src/Network/MessageLayer/PacketType.cs` | Add `GeoEntityOp = 0x36` after `CampaignStateUpdate = 0x34` (`:49`). |
| `src/Network/NetworkEngine.cs` | Add `BroadcastGeoEntityOp(GeoEntityOp)` send helper (after `BroadcastTimingState` `:314`); add `case PacketType.GeoEntityOp:` to `RouteMessage` (after the `CampaignStateUpdate` case `:563`). |
| `Multipleer.Tests/Multipleer.Tests.csproj` | Add `<Compile>` link lines for `GeoEntityOpCodec.cs` and `EntityReplicationScope.cs`. |

**Build:** `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
**Tests:** `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release`
**In-game (2-instance):** per `multipleer-second-instance-setup` (Goldberg-emu second copy + `mklink /J` junctions); see Task 8 checkpoint.

---

## Task 1 — Pure `GeoEntityOpCodec` (struct + enum + round-trip), full TDD

**Files:**
- Create: `src/Network/CommandSync/GeoEntityOpCodec.cs`
- Modify: `Multipleer.Tests/Multipleer.Tests.csproj` (add the `<Compile>` link)
- Test: `Multipleer.Tests/GeoEntityOpCodecTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — Create `Multipleer.Tests/GeoEntityOpCodecTests.cs`:

```csharp
using Multipleer.Network.CommandSync;
using Xunit;

public class GeoEntityOpCodecTests
{
    [Fact]
    public void VehicleCreated_RoundTrips_AllFields()
    {
        var src = new GeoEntityOp
        {
            OpType = GeoEntityOpType.VehicleCreated,
            DefGuid = "VEH_DEF_GUID",
            OwnerFactionGuid = "FAC_PHX_GUID",
            SiteId = 7,
            PosX = 1.5f, PosY = -2.25f, PosZ = 3.75f,
            EntityId = 3
        };
        var back = GeoEntityOpCodec.Decode(GeoEntityOpCodec.Encode(src));
        Assert.Equal(GeoEntityOpType.VehicleCreated, back.OpType);
        Assert.Equal("VEH_DEF_GUID", back.DefGuid);
        Assert.Equal("FAC_PHX_GUID", back.OwnerFactionGuid);
        Assert.Equal(7, back.SiteId);
        Assert.Equal(1.5f, back.PosX);
        Assert.Equal(-2.25f, back.PosY);
        Assert.Equal(3.75f, back.PosZ);
        Assert.Equal(3, back.EntityId);
    }

    [Fact]
    public void VehicleRemoved_RoundTrips()
    {
        var src = new GeoEntityOp { OpType = GeoEntityOpType.VehicleRemoved, EntityId = 5 };
        var back = GeoEntityOpCodec.Decode(GeoEntityOpCodec.Encode(src));
        Assert.Equal(GeoEntityOpType.VehicleRemoved, back.OpType);
        Assert.Equal(5, back.EntityId);
    }

    [Fact]
    public void SiteRemoved_RoundTrips()
    {
        var src = new GeoEntityOp { OpType = GeoEntityOpType.SiteRemoved, SiteId = 42 };
        var back = GeoEntityOpCodec.Decode(GeoEntityOpCodec.Encode(src));
        Assert.Equal(GeoEntityOpType.SiteRemoved, back.OpType);
        Assert.Equal(42, back.SiteId);
    }

    [Fact]
    public void OpTypeBytes_AreStable()
    {
        Assert.Equal((byte)1, (byte)GeoEntityOpType.VehicleCreated);
        Assert.Equal((byte)2, (byte)GeoEntityOpType.VehicleRemoved);
        Assert.Equal((byte)3, (byte)GeoEntityOpType.SiteCreated);
        Assert.Equal((byte)4, (byte)GeoEntityOpType.SiteRemoved);
    }

    [Fact]
    public void NullStrings_EncodeAsEmpty_NoThrow()
    {
        var src = new GeoEntityOp { OpType = GeoEntityOpType.VehicleRemoved, EntityId = 1 };
        // DefGuid / OwnerFactionGuid left null on purpose.
        var back = GeoEntityOpCodec.Decode(GeoEntityOpCodec.Encode(src));
        Assert.Equal("", back.DefGuid);
        Assert.Equal("", back.OwnerFactionGuid);
    }
}
```

- [ ] **Step 2: Run test to verify it fails** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release --filter GeoEntityOpCodecTests`
  Expected: FAIL — compile error (`GeoEntityOp` / `GeoEntityOpType` / `GeoEntityOpCodec` do not exist).

- [ ] **Step 3: Create the pure codec** — Create `src/Network/CommandSync/GeoEntityOpCodec.cs`:

```csharp
using System.IO;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-2: the wire op-types for 0x36 GeoEntityOp. Byte values are STABLE (serialized on the
    // wire) — never renumber. SiteCreated (3) is reserved/forward-compat only; INC-2 neither broadcasts
    // nor applies it (a new site needs a full GeoSiteInstaceData blob -> INC-3 0x35 GeoStateDiff).
    public enum GeoEntityOpType : byte
    {
        VehicleCreated = 1,
        VehicleRemoved = 2,
        SiteCreated = 3, // reserved for INC-3 (needs full site InstanceData)
        SiteRemoved = 4
    }

    // Pure, Unity-free. One authoritative entity create/destroy op. Carries enough to recreate the entity
    // by running its NATIVE lifecycle on the client (def guid + owner faction guid + anchor site/position)
    // and the host's authoritative VehicleID so the client assigns the SAME id (collision-free, §9/C8).
    // No engine types cross the wire — ids/guids are resolved back to live entities at apply time.
    public struct GeoEntityOp
    {
        public GeoEntityOpType OpType;
        public string DefGuid;            // BaseDef.Guid of the vehicle def (VehicleCreated)
        public string OwnerFactionGuid;   // GeoFaction.Def.Guid of the owner (VehicleCreated)
        public int SiteId;                // anchor GeoSite.SiteId (VehicleCreated via site) OR target (SiteRemoved); -1 = none
        public float PosX;                // world position (VehicleCreated via position when SiteId == -1)
        public float PosY;
        public float PosZ;
        public int EntityId;              // authoritative VehicleID (VehicleCreated / VehicleRemoved)
    }

    public static class GeoEntityOpCodec
    {
        public static byte[] Encode(GeoEntityOp op)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)op.OpType);
                bw.Write(op.DefGuid ?? "");
                bw.Write(op.OwnerFactionGuid ?? "");
                bw.Write(op.SiteId);
                bw.Write(op.PosX);
                bw.Write(op.PosY);
                bw.Write(op.PosZ);
                bw.Write(op.EntityId);
                return ms.ToArray();
            }
        }

        public static GeoEntityOp Decode(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new GeoEntityOp
                {
                    OpType = (GeoEntityOpType)br.ReadByte(),
                    DefGuid = br.ReadString(),
                    OwnerFactionGuid = br.ReadString(),
                    SiteId = br.ReadInt32(),
                    PosX = br.ReadSingle(),
                    PosY = br.ReadSingle(),
                    PosZ = br.ReadSingle(),
                    EntityId = br.ReadInt32()
                };
            }
        }
    }
}
```

- [ ] **Step 4: Link the pure codec into the test assembly** — In `Multipleer.Tests/Multipleer.Tests.csproj`, immediately after the `GeoSimProducerTable.cs` link line (`:30`), add:

```xml
    <Compile Include="..\src\Network\CommandSync\GeoEntityOpCodec.cs"><Link>GeoEntityOpCodec.cs</Link></Compile>
```

- [ ] **Step 5: Run test to verify it passes** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release --filter GeoEntityOpCodecTests`
  Expected: PASS (all 5 cases).

- [ ] **Step 6: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multipleer add -A && git -C E:\DEV\PhoenixPoint\Multipleer commit -m "feat(replication): pure GeoEntityOp codec for 0x36 (4 op-types, TDD)"
```

---

## Task 2 — Pure `EntityReplicationScope` re-entrancy guard, full TDD

**Files:**
- Create: `src/Network/CommandSync/EntityReplicationScope.cs`
- Modify: `Multipleer.Tests/Multipleer.Tests.csproj` (add the `<Compile>` link)
- Test: `Multipleer.Tests/EntityReplicationScopeTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — Create `Multipleer.Tests/EntityReplicationScopeTests.cs`:

```csharp
using Multipleer.Network.CommandSync;
using Xunit;

public class EntityReplicationScopeTests
{
    [Fact]
    public void IsApplying_DefaultsFalse()
    {
        Assert.False(EntityReplicationScope.IsApplying);
    }

    [Fact]
    public void IsApplying_TrueInsideScope_RestoredAfter()
    {
        Assert.False(EntityReplicationScope.IsApplying);
        using (EntityReplicationScope.Enter())
        {
            Assert.True(EntityReplicationScope.IsApplying);
        }
        Assert.False(EntityReplicationScope.IsApplying);
    }

    [Fact]
    public void NestedScopes_RestoreOuterState()
    {
        using (EntityReplicationScope.Enter())
        {
            Assert.True(EntityReplicationScope.IsApplying);
            using (EntityReplicationScope.Enter())
            {
                Assert.True(EntityReplicationScope.IsApplying);
            }
            Assert.True(EntityReplicationScope.IsApplying); // inner dispose must not clear outer
        }
        Assert.False(EntityReplicationScope.IsApplying);
    }
}
```

- [ ] **Step 2: Run test to verify it fails** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release --filter EntityReplicationScopeTests`
  Expected: FAIL — compile error (`EntityReplicationScope` does not exist).

- [ ] **Step 3: Create the pure scope** — Create `src/Network/CommandSync/EntityReplicationScope.cs`:

```csharp
using System;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-2: [ThreadStatic] re-entrancy guard for client-side entity-op replay. Mirrors
    // CommandRelay's IsApplying guard (CommandRelay.cs:26-27). The ClientEntityOpApplier wraps the
    // native CreateVehicle/Destroy/DestroySite call in `using (EntityReplicationScope.Enter())` so the
    // birth/death postfixes those native calls trigger (HostEntityOpBroadcastPatch) recognize a replay
    // and do NOT re-broadcast it (host) / do not recurse. Nested-safe (restores the prior value).
    public static class EntityReplicationScope
    {
        [ThreadStatic] private static bool _applying;
        public static bool IsApplying => _applying;

        public static IDisposable Enter() => new Token();

        private sealed class Token : IDisposable
        {
            private readonly bool _prev;
            public Token() { _prev = _applying; _applying = true; }
            public void Dispose() { _applying = _prev; }
        }
    }
}
```

- [ ] **Step 4: Link the pure scope into the test assembly** — In `Multipleer.Tests/Multipleer.Tests.csproj`, immediately after the `GeoEntityOpCodec.cs` link line added in Task 1, add:

```xml
    <Compile Include="..\src\Network\CommandSync\EntityReplicationScope.cs"><Link>EntityReplicationScope.cs</Link></Compile>
```

- [ ] **Step 5: Run test to verify it passes** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release --filter EntityReplicationScopeTests`
  Expected: PASS (all 3 cases).

- [ ] **Step 6: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multipleer add -A && git -C E:\DEV\PhoenixPoint\Multipleer commit -m "feat(replication): EntityReplicationScope [ThreadStatic] guard for entity-op replay (TDD)"
```

---

## Task 3 — `PacketType.GeoEntityOp` (0x36) + `NetworkEngine` send helper + route, build-verified

**Files:**
- Modify: `src/Network/MessageLayer/PacketType.cs:49`
- Modify: `src/Network/NetworkEngine.cs:314` (send helper) and `:563` (route case)

> No unit test — these are wiring edits over the live `NetworkMessage`/transport. Verification = compiles + the codec (Task 1) and applier (Task 5) cover the encode/decode/apply. The send helper + route mirror the proven `BroadcastTimingState`/`CampaignStateUpdate` path exactly.

- [ ] **Step 1: Add the packet type** — In `src/Network/MessageLayer/PacketType.cs`, change the Campaign Actions block (currently ending `CampaignStateUpdate = 0x34,` at `:49`) to add the new line right after it:

```csharp
        // Campaign Actions
        CampaignActionRequest = 0x30,
        CampaignActionApproved = 0x31,
        CampaignActionRejected = 0x32,
        CampaignActionResult = 0x33,
        CampaignStateUpdate = 0x34,
        // 0x35 GeoStateDiff reserved for INC-3 (generic InstanceData-diff) — intentionally skipped here.
        GeoEntityOp = 0x36,
```

- [ ] **Step 2: Add the reliable send helper** — In `src/Network/NetworkEngine.cs`, immediately after the `BroadcastTimingState` method (it ends at `:314` with the `BroadcastToAll(msg);` + closing brace), insert:

```csharp
        // Host -> all: authoritative entity create/destroy op (0x36 GeoEntityOp). RELIABLE + ordered
        // (BroadcastToAll). The body is the pure GeoEntityOpCodec image; clients decode + apply via
        // ClientEntityOpApplier under EntityReplicationScope. Mirrors BroadcastTimingState.
        public void BroadcastGeoEntityOp(Multipleer.Network.CommandSync.GeoEntityOp op)
        {
            var body = Multipleer.Network.CommandSync.GeoEntityOpCodec.Encode(op);
            var msg = new NetworkMessage(PacketType.GeoEntityOp, body);
            BroadcastToAll(msg);
        }
```

- [ ] **Step 3: Add the route case** — In `src/Network/NetworkEngine.cs` `RouteMessage`, immediately after the `case PacketType.CampaignStateUpdate:` block (it ends at `:563` with `break;`), insert:

```csharp
                case PacketType.GeoEntityOp:
                    var entityOp = Multipleer.Network.CommandSync.GeoEntityOpCodec.Decode(msg.Payload);
                    Multipleer.Network.CommandSync.ClientEntityOpApplier.Apply(entityOp);
                    break;
```

> NOTE: `ClientEntityOpApplier` is created in Task 5. To keep THIS task building in isolation, Task 5 must land before a full build; sequence Tasks 3→4→5 then build. If you build after Step 3 alone it will fail on `ClientEntityOpApplier` — that is expected; the Step-4 build below is the first green checkpoint and assumes Task 5 is also applied. If executing strictly one task at a time, add a temporary no-op `ClientEntityOpApplier.Apply` stub here and replace it in Task 5; the cleaner path is to do Tasks 3–5 as a unit before building.

- [ ] **Step 4: Build to verify it compiles (after Task 5 lands)** —
  Run: `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multipleer add -A && git -C E:\DEV\PhoenixPoint\Multipleer commit -m "feat(replication): wire 0x36 GeoEntityOp packet + BroadcastGeoEntityOp + route"
```

---

## Task 4 — `GeoBridge` helpers for entity-op apply (def/faction/site resolve), build-verified

**Files:**
- Modify: `src/Network/CommandSync/GeoBridge.cs` (add resolver helpers)

> No unit test — these are `AccessTools` reflection helpers over live game types (same as the existing `GeoBridge` members). Verification = compiles + used by the Task-5 applier + Task-8 in-game.

- [ ] **Step 1: Add the resolver helpers** — In `src/Network/CommandSync/GeoBridge.cs`, inside the `GeoBridge` class (after the existing `SiteId(object)` method at `:78`, before the closing brace `:79`), add:

```csharp
        // DefRepository.GetDef(string guid) -> BaseDef, via GameUtl.GameComponent<DefRepository>().
        // Returns the def object (ComponentSetDef-derived for vehicles) or null.
        public static object FindDefByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            var defRepoType = AccessTools.TypeByName("Base.Defs.DefRepository");
            var gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl");
            if (defRepoType == null || gameUtlType == null) return null;
            // GameUtl.GameComponent<DefRepository>() — generic static; make + invoke.
            var generic = AccessTools.Method(gameUtlType, "GameComponent");
            var repo = generic?.MakeGenericMethod(defRepoType)?.Invoke(null, null);
            if (repo == null) return null;
            return AccessTools.Method(repo.GetType(), "GetDef", new[] { typeof(string) })
                              ?.Invoke(repo, new object[] { guid });
        }

        // Resolve a GeoFaction by its Def.Guid from GeoLevelController.Factions; falls back to
        // PhoenixFaction (the common INC-2 case: manufactured aircraft) when guid is empty/unmatched.
        public static object FindFactionByGuid(object geoLevel, string factionGuid)
        {
            var phoenix = AccessTools.Property(geoLevel.GetType(), "PhoenixFaction")?.GetValue(geoLevel);
            if (string.IsNullOrEmpty(factionGuid)) return phoenix;
            var factions = AccessTools.Field(geoLevel.GetType(), "Factions")?.GetValue(geoLevel) as IEnumerable;
            if (factions == null) return phoenix;
            foreach (var f in factions)
                if (FactionGuid(f) == factionGuid) return f;
            return phoenix;
        }

        // GeoFaction.Def.Guid (Def -> BaseDef.Guid). Empty string if unresolved.
        public static string FactionGuid(object faction)
        {
            var def = AccessTools.Property(faction.GetType(), "Def")?.GetValue(faction);
            if (def == null) return "";
            return AccessTools.Field(def.GetType(), "Guid")?.GetValue(def)?.ToString() ?? "";
        }

        // BaseDef.Guid of a vehicle's def: GeoVehicle.VehicleDef.Guid (VehicleDef -> BaseDef.Guid).
        public static string VehicleDefGuid(object vehicle)
        {
            var def = AccessTools.Property(vehicle.GetType(), "VehicleDef")?.GetValue(vehicle);
            if (def == null) return "";
            return AccessTools.Field(def.GetType(), "Guid")?.GetValue(def)?.ToString() ?? "";
        }

        // GeoSite by int SiteId (string key), scanning GeoMap.AllSites. Null if not found.
        public static object FindSiteById(object geoLevel, int siteId)
        {
            var map = AccessTools.Field(geoLevel.GetType(), "Map")?.GetValue(geoLevel);
            var sites = AccessTools.Property(map?.GetType(), "AllSites")?.GetValue(map) as IEnumerable;
            if (sites == null) return null;
            foreach (var s in sites)
                if (SiteId(s) == siteId.ToString()) return s;
            return null;
        }

        // GeoFaction.CreateVehicle(GeoSite, ComponentSetDef) — runs the full native lifecycle
        // (Instantiate -> DoEnterPlay -> OnLevelStart -> TeleportToSite -> VehicleAdded). Returns the
        // new GeoVehicle, or null if the method/types are unresolved.
        public static object CreateVehicleAtSite(object faction, object site, object vehicleDef)
        {
            var siteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var csdType = AccessTools.TypeByName("Base.Core.ComponentSetDef");
            if (siteType == null || csdType == null) return null;
            var m = AccessTools.Method(faction.GetType(), "CreateVehicle", new[] { siteType, csdType });
            return m?.Invoke(faction, new[] { site, vehicleDef });
        }

        // GeoFaction.CreateVehicleAtPosition(Vector3, ComponentSetDef) — full native lifecycle (pos path).
        public static object CreateVehicleAtPosition(object faction, Vector3 pos, object vehicleDef)
        {
            var csdType = AccessTools.TypeByName("Base.Core.ComponentSetDef");
            if (csdType == null) return null;
            var m = AccessTools.Method(faction.GetType(), "CreateVehicleAtPosition",
                new[] { typeof(Vector3), csdType });
            return m?.Invoke(faction, new object[] { pos, vehicleDef });
        }

        // Reconcile the new vehicle's id to the host's authoritative VehicleID and clamp the faction's
        // private _lastVehicleIndex so it never re-issues that id (collision-free, §9/C8).
        public static void ReconcileVehicleId(object faction, object vehicle, int authoritativeId)
        {
            AccessTools.Field(vehicle.GetType(), "VehicleID")?.SetValue(vehicle, authoritativeId);
            var fld = AccessTools.Field(faction.GetType(), "_lastVehicleIndex");
            var cur = fld?.GetValue(faction);
            if (cur is int c && authoritativeId > c)
                fld.SetValue(faction, authoritativeId);
        }
```

> The `using UnityEngine;` directive must be present in `GeoBridge.cs` for the `Vector3` reference. It currently imports `System`, `System.Collections`, `System.Collections.Generic`, `HarmonyLib` (`GeoBridge.cs:1-4`). Add `using UnityEngine;` at the top of the file.

- [ ] **Step 2: Add the UnityEngine import** — At the top of `src/Network/CommandSync/GeoBridge.cs`, after `using HarmonyLib;` (`:4`), add:

```csharp
using UnityEngine;
```

- [ ] **Step 3: Build to verify it compiles (after Task 5 lands)** —
  Run: `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multipleer add -A && git -C E:\DEV\PhoenixPoint\Multipleer commit -m "feat(replication): GeoBridge entity-op resolvers (def/faction/site + native create + id reconcile)"
```

---

## Task 5 — `ClientEntityOpApplier` (native lifecycle replay under replication scope), build-verified

**Files:**
- Create: `src/Network/CommandSync/ClientEntityOpApplier.cs`

> No unit test — engine reflection + native lifecycle over live types. Verification = compiles + Task-8 in-game. (The pure codec/scope it consumes are covered by Tasks 1-2.)

- [ ] **Step 1: Create the applier** — Create `src/Network/CommandSync/ClientEntityOpApplier.cs`:

```csharp
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-2 (C): client-only apply of a host 0x36 GeoEntityOp. Runs the NATIVE entity lifecycle
    // (no hand-built visuals) under EntityReplicationScope so the birth/death postfixes the replay
    // triggers (HostEntityOpBroadcastPatch) recognize a replay and do not re-broadcast.
    //   * VehicleCreated -> GeoFaction.CreateVehicle(GeoSite, ComponentSetDef) [Instantiate -> DoEnterPlay
    //     -> OnLevelStart -> TeleportToSite -> VehicleAdded; marker comes from the lifecycle, C18 -> we do
    //     NOT gate VehicleAdded], then reconcile VehicleID/_lastVehicleIndex to the host's authoritative id
    //     so a later StartTravel for that id resolves in GeoBridge.FindVehicleById.
    //   * VehicleRemoved -> GeoVehicle.Destroy().
    //   * SiteRemoved   -> GeoSite.DestroySite().
    //   * SiteCreated   -> NOT applied (needs full GeoSiteInstaceData -> INC-3 0x35); logged + ignored.
    public static class ClientEntityOpApplier
    {
        public static void Apply(GeoEntityOp op)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return; // client-only

            var geoLevel = GeoBridge.GetGeoLevelController();
            if (geoLevel == null) { Debug.LogWarning("[Multipleer] EntityOp apply: no GeoLevelController."); return; }

            using (EntityReplicationScope.Enter())
            {
                switch (op.OpType)
                {
                    case GeoEntityOpType.VehicleCreated: ApplyVehicleCreated(geoLevel, op); break;
                    case GeoEntityOpType.VehicleRemoved: ApplyVehicleRemoved(geoLevel, op); break;
                    case GeoEntityOpType.SiteRemoved:    ApplySiteRemoved(geoLevel, op); break;
                    case GeoEntityOpType.SiteCreated:
                        Debug.Log("[Multipleer] EntityOp: SiteCreated deferred to INC-3 (needs site InstanceData).");
                        break;
                }
            }
        }

        private static void ApplyVehicleCreated(object geoLevel, GeoEntityOp op)
        {
            // Idempotency: if the vehicle id already exists (duplicate op / reload race), skip.
            if (GeoBridge.FindVehicleById(geoLevel, op.EntityId.ToString()) != null)
            {
                Debug.Log($"[Multipleer] EntityOp VehicleCreated: id {op.EntityId} already present, skip.");
                return;
            }

            var def = GeoBridge.FindDefByGuid(op.DefGuid);
            if (def == null) { Debug.LogWarning($"[Multipleer] VehicleCreated: def {op.DefGuid} not resolved."); return; }

            var faction = GeoBridge.FindFactionByGuid(geoLevel, op.OwnerFactionGuid);
            if (faction == null) { Debug.LogWarning("[Multipleer] VehicleCreated: owner faction not resolved."); return; }

            object vehicle;
            if (op.SiteId >= 0)
            {
                var site = GeoBridge.FindSiteById(geoLevel, op.SiteId);
                if (site == null) { Debug.LogWarning($"[Multipleer] VehicleCreated: anchor site {op.SiteId} not found."); return; }
                vehicle = GeoBridge.CreateVehicleAtSite(faction, site, def);
            }
            else
            {
                vehicle = GeoBridge.CreateVehicleAtPosition(faction, new Vector3(op.PosX, op.PosY, op.PosZ), def);
            }

            if (vehicle == null) { Debug.LogError("[Multipleer] VehicleCreated: native CreateVehicle returned null."); return; }
            GeoBridge.ReconcileVehicleId(faction, vehicle, op.EntityId);
            Debug.Log($"[Multipleer] EntityOp VehicleCreated: spawned + reconciled VehicleID {op.EntityId}.");
        }

        private static void ApplyVehicleRemoved(object geoLevel, GeoEntityOp op)
        {
            var vehicle = GeoBridge.FindVehicleById(geoLevel, op.EntityId.ToString());
            if (vehicle == null) { Debug.Log($"[Multipleer] VehicleRemoved: id {op.EntityId} absent, nothing to remove."); return; }
            AccessTools.Method(vehicle.GetType(), "Destroy")?.Invoke(vehicle, null);
            Debug.Log($"[Multipleer] EntityOp VehicleRemoved: destroyed VehicleID {op.EntityId}.");
        }

        private static void ApplySiteRemoved(object geoLevel, GeoEntityOp op)
        {
            var site = GeoBridge.FindSiteById(geoLevel, op.SiteId);
            if (site == null) { Debug.Log($"[Multipleer] SiteRemoved: site {op.SiteId} absent."); return; }
            AccessTools.Method(site.GetType(), "DestroySite")?.Invoke(site, null);
            Debug.Log($"[Multipleer] EntityOp SiteRemoved: destroyed SiteId {op.SiteId}.");
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles** —
  Run: `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. (This is the first green build for Tasks 3–5 combined.)

- [ ] **Step 3: Run full suite to verify no regression** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release`
  Expected: all tests PASS (Task 1+2 cases + every pre-existing test green).

- [ ] **Step 4: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multipleer add -A && git -C E:\DEV\PhoenixPoint\Multipleer commit -m "feat(replication): ClientEntityOpApplier - native lifecycle replay + VehicleID reconcile (client-only)"
```

---

## Task 6 — `HostEntityOpBroadcastPatch` (host postfix on create/remove seams), build-verified

**Files:**
- Create: `src/Harmony/HostEntityOpBroadcastPatch.cs`

> No unit test — Harmony multi-target patch over `GeoFaction`/`GeoSite`; not in the Unity-free test set (like every intercept patch). Verification = compiles (`Prepare` links) + Task-8 in-game.

- [ ] **Step 1: Create the patch** — Create `src/Harmony/HostEntityOpBroadcastPatch.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using UnityEngine;

namespace Multipleer.Harmony
{
    // SD-AIDR INC-2 (B): on the HOST, broadcast an entity create/destroy op (0x36) whenever the native
    // birth/death seams fire, so clients replicate the entity. Mirrors StartTravelInterceptPatch.Postfix:
    // host-only gate; bail during a relayed/replayed apply so we never echo a replicated op.
    //   * GeoFaction.CreateVehicle(GeoSite, ComponentSetDef)        -> VehicleCreated (site anchor)
    //   * GeoFaction.CreateVehicleAtPosition(Vector3, ComponentSetDef) -> VehicleCreated (position)
    //   * GeoFaction.UnregisterVehicle(GeoVehicle)                  -> VehicleRemoved (single removal choke)
    //   * GeoSite.DestroySite()                                     -> SiteRemoved
    // SiteCreated is NOT emitted in INC-2 (a new site needs full InstanceData -> INC-3 0x35).
    [HarmonyPatch]
    public static class HostEntityOpBroadcastPatch
    {
        private static MethodBase _createVehicle;
        private static MethodBase _createVehicleAtPos;
        private static MethodBase _unregisterVehicle;
        private static MethodBase _destroySite;

        public static bool Prepare()
        {
            var faction = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            var site = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var csdType = AccessTools.TypeByName("Base.Core.ComponentSetDef");
            var vehType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (faction == null || site == null || csdType == null || vehType == null) return false;

            _createVehicle = AccessTools.Method(faction, "CreateVehicle", new[] { geoSiteType, csdType });
            _createVehicleAtPos = AccessTools.Method(faction, "CreateVehicleAtPosition",
                new[] { typeof(Vector3), csdType });
            _unregisterVehicle = AccessTools.Method(faction, "UnregisterVehicle", new[] { vehType });
            _destroySite = AccessTools.Method(site, "DestroySite", Type.EmptyTypes);

            // Patch the class if at least one seam resolved (best-effort).
            return _createVehicle != null || _createVehicleAtPos != null
                || _unregisterVehicle != null || _destroySite != null;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            if (_createVehicle != null) yield return _createVehicle;
            if (_createVehicleAtPos != null) yield return _createVehicleAtPos;
            if (_unregisterVehicle != null) yield return _unregisterVehicle;
            if (_destroySite != null) yield return _destroySite;
        }

        // __originalMethod tells us which seam fired (one Postfix, four targets). __instance is the
        // GeoFaction (vehicle seams) or GeoSite (DestroySite); __result is the new GeoVehicle for the
        // create seams (object so the mod never references GeoVehicle). For UnregisterVehicle the removed
        // vehicle arrives as the first parameter (__0). For DestroySite there are no params.
        public static void Postfix(object __instance, object __result, MethodBase __originalMethod, object __0)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return; // host emits authoritatively
            if (CommandRelay.IsApplying) return;            // a relayed command apply already fans out
            if (EntityReplicationScope.IsApplying) return;  // a client replay (never on host, defensive)

            var name = __originalMethod.Name;
            try
            {
                if (name == "CreateVehicle" || name == "CreateVehicleAtPosition")
                {
                    var vehicle = __result; // the new GeoVehicle
                    if (vehicle == null) return;
                    var op = new GeoEntityOp
                    {
                        OpType = GeoEntityOpType.VehicleCreated,
                        DefGuid = GeoBridge.VehicleDefGuid(vehicle),
                        OwnerFactionGuid = GeoBridge.FactionGuid(__instance),
                        SiteId = ResolveCurrentSiteId(vehicle),
                        EntityId = int.Parse(GeoBridge.VehicleId(vehicle))
                    };
                    // Position fallback when the vehicle is not anchored on a site (CreateVehicleAtPosition).
                    if (op.SiteId < 0)
                    {
                        var pos = WorldPositionOf(vehicle);
                        op.PosX = pos.x; op.PosY = pos.y; op.PosZ = pos.z;
                    }
                    engine.BroadcastGeoEntityOp(op);
                }
                else if (name == "UnregisterVehicle")
                {
                    var vehicle = __0; // the removed GeoVehicle
                    if (vehicle == null) return;
                    engine.BroadcastGeoEntityOp(new GeoEntityOp
                    {
                        OpType = GeoEntityOpType.VehicleRemoved,
                        SiteId = -1,
                        EntityId = int.Parse(GeoBridge.VehicleId(vehicle))
                    });
                }
                else if (name == "DestroySite")
                {
                    var site = __instance; // the GeoSite being destroyed
                    engine.BroadcastGeoEntityOp(new GeoEntityOp
                    {
                        OpType = GeoEntityOpType.SiteRemoved,
                        SiteId = int.Parse(GeoBridge.SiteId(site)),
                        EntityId = -1
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multipleer] HostEntityOpBroadcastPatch ({name}) failed: {ex}");
            }
        }

        // GeoVehicle.CurrentSite?.SiteId, or -1 if travelling / no site (use position instead).
        private static int ResolveCurrentSiteId(object vehicle)
        {
            var site = AccessTools.Property(vehicle.GetType(), "CurrentSite")?.GetValue(vehicle);
            if (site == null) return -1;
            var s = GeoBridge.SiteId(site);
            return int.TryParse(s, out var id) ? id : -1;
        }

        // ActorComponent.WorldPosition (base) — Vector3.
        private static Vector3 WorldPositionOf(object vehicle)
        {
            var v = AccessTools.Property(vehicle.GetType(), "WorldPosition")?.GetValue(vehicle);
            return v is Vector3 p ? p : Vector3.zero;
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles** —
  Run: `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run full suite to verify no regression** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release`
  Expected: all tests PASS.

- [ ] **Step 4: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multipleer add -A && git -C E:\DEV\PhoenixPoint\Multipleer commit -m "feat(replication): HostEntityOpBroadcastPatch - broadcast 0x36 on create/remove seams (host-only)"
```

---

## Task 7 — Full build + suite green gate (pre-checkpoint)

**Files:** none (verification only).

- [ ] **Step 1: Clean build** —
  Run: `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 2: Full test suite** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release`
  Expected: ALL tests PASS — the 5 `GeoEntityOpCodecTests` + 3 `EntityReplicationScopeTests` + every pre-existing test (CommandCodec, GeoSimProducerTable, InterceptRegistry, transport, roster, chat, etc.). No failures, no skips beyond pre-existing.

- [ ] **Step 3: Deploy the built DLL to BOTH instances** — Per `multipleer-second-instance-setup`: the DLL output (`Multipleer.csproj` build) reaches the live mod folder; with `mklink /J` junctions the second Goldberg-emu copy shares it. Confirm both instances will load the NEW build (check the DLL timestamp under each instance's mod folder). No commit (build/deploy only).

---

## Task 8 — In-game 2-instance checkpoint (USER runs this)

**Files:** none (integration verification only).

> Harmony/Unity patches are not unit-testable. Final verification is a 2-instance in-game run per the `multipleer-second-instance-setup` memory (Goldberg-emu second copy + `mklink /J` junctions). The USER performs this; the agent records the outcome.

- [ ] **Step 1: Launch two instances + reach geoscape** — Start the host instance + the Goldberg-emu second instance. Host loads a geoscape campaign; the second instance joins (lobby → ready → host picks save → transfer → barrier → play). Confirm BOTH reach the geoscape and the INC-1 baseline holds (existing vehicles fly in sync, client clock ticks).

- [ ] **Step 2: Host acquires a NEW aircraft → it APPEARS on the client (acceptance #2, the root-cause fix)** — On the HOST, obtain a NEW Phoenix aircraft that did not exist at save-transfer time. Easiest: finish manufacturing a new aircraft, OR use the host's console `CreateVehicle` if available, OR start the campaign with a known aircraft count and add one. EXPECT on the CLIENT: the new aircraft's map marker APPEARS at the same site/position as on the host (the host `CreateVehicle`/`CreateVehicleAtPosition` postfix broadcast `0x36 VehicleCreated`; the client ran the native lifecycle → marker from `VehicleAdded`). Confirm the client did NOT previously show this vehicle and now does.

- [ ] **Step 3: Host orders the NEW aircraft to travel → it FLIES on the client (acceptance #1 for a host-created vehicle; the "vehicle N not found" fix)** — On the HOST, order the just-created aircraft to travel to a distant site. EXPECT on the CLIENT: the same aircraft flies along the same path on the slaved clock (whitelisted `NavigateRoutine`), and the client log shows NO `"StartTravel apply: vehicle N not found"` warning (`CommandExecutor.cs:37`) — because `GeoBridge.FindVehicleById` now resolves the replicated id. This is the confirmed root-cause failure being gone.

- [ ] **Step 4: Host-created/destroyed SITE removal mirrors on the client (SiteRemoved)** — On the HOST, cause a site to be destroyed/removed (e.g. complete/clear a mission site that despawns, or a scavenging/ancient site that is consumed). EXPECT on the CLIENT: that site's marker VANISHES (host `GeoSite.DestroySite()` postfix → `0x36 SiteRemoved` → client `DestroySite()`). NOTE: a NEW site APPEARING on the client (SiteCreated) is NOT expected in INC-2 — it is deferred to INC-3; if a new host mission-site does not appear on the client yet, that is the documented boundary, not a bug.

- [ ] **Step 5: Confirm the documented INC-2 boundary (do not over-claim)** — After the new aircraft FLIES and visually reaches its destination on the client, the client's authoritative `CurrentSite`/landing/occupancy for that vehicle MAY remain stale (the three travel emitters are still suppressed by INC-1's `ClientTravelEmitterSuppressPatch`, and they fire from the `NavigateRoutine` coroutine AFTER the replication scope reset). EXPECT: ship RENDERS + FLIES (PASS) but client-side landing state lag is ACCEPTED until INC-3. Record this explicitly so it is not mistaken for a regression.

- [ ] **Step 6: Confirm the host is unaffected** — On the HOST, vehicle creation/destruction and travel behave fully normally; the broadcast is additive (a postfix that only sends). No host-side artifacts.

- [ ] **Step 7: Record the outcome** — Append the result (PASS/FAIL per step, any `vehicle N not found` residue, any id-collision or duplicate-marker note, the SiteRemoved result, and the confirmed INC-2 boundary) to `docs/research/00-current-state.md` via SCRIBE, and update the in-game-test status for SD-AIDR INC-2.

---

## Self-Review

**1. Spec coverage (INC-2 scope A/B/C/D):**
- **A. NEW packet `0x36 GeoEntityOp` (reliable, ordered): op-type {VehicleCreated, VehicleRemoved, SiteCreated, SiteRemoved}, payload sufficient to recreate (def id, owner faction, position/site ref, authoritative VehicleID, key spawn params) + codec (pure, TDD) + serializer + PacketType + NetworkEngine route + broadcast** → Task 1 (pure `GeoEntityOpCodec`, all 4 op-types, TDD round-trip; payload = OpType/DefGuid/OwnerFactionGuid/SiteId/Pos/EntityId) + Task 3 (`PacketType.GeoEntityOp = 0x36`, `BroadcastGeoEntityOp` reliable via `BroadcastToAll`, `RouteMessage` case → `ClientEntityOpApplier.Apply`). ✓
- **B. HOST side: postfix on CreateVehicle/CreateVehicleAtPosition (+ VehicleRemoved, site remove) → broadcast; gate host-only; reuse StartTravelInterceptPatch.Postfix pattern** → Task 6 (`HostEntityOpBroadcastPatch` multi-target postfix on `CreateVehicle`/`CreateVehicleAtPosition`/`UnregisterVehicle`/`DestroySite`; host-only gate + `CommandRelay.IsApplying`/`EntityReplicationScope.IsApplying` bail, exactly mirroring `StartTravelInterceptPatch.Postfix`). Site ADD broadcast is intentionally deferred (SiteCreated → INC-3, documented). ✓
- **C. CLIENT side: apply under IsApplying → recreate/destroy via native lifecycle (Initialize/DoEnterPlay), assign authoritative VehicleID + reconcile `_lastVehicleIndex`; do NOT gate VehicleAdded** → Task 5 (`ClientEntityOpApplier` under `EntityReplicationScope.Enter()`; `VehicleCreated` → `GeoFaction.CreateVehicle` native lifecycle which fires `VehicleAdded` itself (un-gated, C18) → `GeoBridge.ReconcileVehicleId` sets `VehicleID` + clamps `_lastVehicleIndex`; `VehicleRemoved` → `Destroy()`; `SiteRemoved` → `DestroySite()`) + Task 2 (the `EntityReplicationScope` guard, TDD) + Task 4 (`GeoBridge` resolvers). ✓
- **D. Verify host→client StartTravel for a host-created vehicle resolves (ship appears AND flies via whitelisted NavigateRoutine on slaved clock)** → Task 8 Steps 2-3 (appears, then flies, no `vehicle N not found`). ✓
- **Testing reality (codec/serializer pure → TDD; host postfixes + client replay Unity-dependent → build-verified + in-game)** → Tasks 1-2 TDD-first; Tasks 3-6 build-verified + suite-green; Task 7 gate; Task 8 in-game. ✓
- **Boundary honesty (arrival/CurrentSite stale until INC-3; emitters fire from NavigateRoutine after IsApplying resets)** → stated in Grounding "Documented boundaries" + Task 8 Step 5. ✓

**2. Placeholder scan:** No "TBD/TODO/handle edge cases/similar to Task N". Every code step shows COMPLETE code; every command shows expected output. The Task-3 build-sequencing note is an explicit ordering instruction (Tasks 3–5 build as a unit), not a placeholder — it names the exact symptom and the exact resolution. ✓

**3. Type/name consistency:**
- `GeoEntityOp` struct fields (`OpType`/`DefGuid`/`OwnerFactionGuid`/`SiteId`/`PosX`/`PosY`/`PosZ`/`EntityId`) defined in Task 1, used identically in the codec tests (Task 1), `BroadcastGeoEntityOp` (Task 3), `ClientEntityOpApplier` (Task 5), `HostEntityOpBroadcastPatch` (Task 6). ✓
- `GeoEntityOpType` byte values (1/2/3/4) pinned in Task 1 enum + asserted stable in `GeoEntityOpCodecTests.OpTypeBytes_AreStable`; consumed by the same names everywhere. ✓
- `GeoEntityOpCodec.Encode`/`Decode` (Task 1) ↔ `BroadcastGeoEntityOp` encode (Task 3) ↔ `RouteMessage` decode (Task 3). ✓
- `EntityReplicationScope.IsApplying`/`Enter()` (Task 2) ↔ used in `ClientEntityOpApplier` (Task 5) + `HostEntityOpBroadcastPatch` (Task 6). ✓
- `GeoBridge` new members `FindDefByGuid`/`FindFactionByGuid`/`FactionGuid`/`VehicleDefGuid`/`FindSiteById`/`CreateVehicleAtSite`/`CreateVehicleAtPosition`/`ReconcileVehicleId` (Task 4) ↔ called by exactly those names in `ClientEntityOpApplier` (Task 5) and `HostEntityOpBroadcastPatch` (Task 6); existing `VehicleId`/`SiteId`/`FindVehicleById`/`GetGeoLevelController` reused unchanged. ✓
- `PacketType.GeoEntityOp` (Task 3) ↔ `NetworkMessage(PacketType.GeoEntityOp, ...)` + route case. ✓
- Client gate (`NetworkEngine.Instance`/`IsActive`/`IsHost`) + `CommandRelay.IsApplying` match the live accessors in `NetworkEngine.cs:11-15` and `CommandRelay.cs:27`. ✓
- Native member names verified against decompile: `GeoFaction.CreateVehicle(GeoSite,ComponentSetDef)` `:2011`, `CreateVehicleAtPosition(Vector3,ComponentSetDef)` `:2028`, `UnregisterVehicle(GeoVehicle)` `:2084`, `_lastVehicleIndex` `:73`, `GeoVehicle.VehicleID` `:51`, `Destroy()` `:599`, `GeoSite.DestroySite()` `:849`, `BaseDef.Guid` `:21`, `DefRepository.GetDef(string)` `:70`, `GeoLevelController.Factions` `:85`, `GeoFaction.PhoenixFaction`/`Def` `:121`. ✓

No gaps found.

---

## Out of scope (later increments — do NOT build here)
- **INC 3** — `0x35 GeoStateDiff` (generic per-entity InstanceData-diff) + marketplace blob + haven-defense progress; this is where `SiteCreated` (full `GeoSiteInstaceData`) and client-side arrival/`CurrentSite`/occupancy authority land. INC-2's documented boundary (ship flies but lands stale; new sites do not appear) is closed by INC-3.
- **INC 4** — input generalization (`GeoAbility.Activate` → CommandSync, 18 intents; launch-loop gating; retire the per-action `StartTravel` patch).
- **INC 5** — rolling CRC32 divergence detection + two-barrier reload backstop.
