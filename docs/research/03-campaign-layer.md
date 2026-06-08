# Campaign Layer Systems — Permission Injection Points

Source: `decompiled\AssemblyCSharp\Assembly-CSharp\src`

> This is the source-dive identifying campaign subsystem entry points and where permission checks can be injected. The campaign stays a **shared** resource; the host assigns permissions to players and the enforcement is a Harmony **prefix that rejects the call when the acting player lacks permission**. Host-side assignment UI + cross-session persistence → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md) §4.

## 1. Research System

```csharp
// Namespace: PhoenixPoint.Geoscape.Entities
```

### Key Methods

| Method | Signature | Access |
|--------|-----------|--------|
| `Research.SetQueued()` | `public void SetQueued(ResearchDef def, bool manualAdd)` | public |
| `ResearchElement.AddResearch()` | `public void AddResearch(int points)` | public |
| `Research.StartNextQueuedResearch()` | `private void StartNextQueuedResearch()` | private |

### Injection Strategy

| Target | Harmony Type | Purpose |
|--------|-------------|---------|
| `Research.SetQueued(ResearchDef, bool)` | Prefix | Validate permission before adding research to queue |
| `ResearchElement.AddResearch(int)` | Prefix | Validate permission before adding progress |

**Events:** `GeoFaction.ResearchStartedEventHandler`, `ResearchCompletedEventHandler`

---

## 2. Manufacturing System

```csharp
// Namespace: PhoenixPoint.Geoscape.Entities
```

### Key Methods

| Method | Signature | Access |
|--------|-----------|--------|
| `ItemManufacturing.EnqueueItem()` | `public void EnqueueItem(ManufacturableItem item)` | public |
| `ItemManufacturing.RemoveFromQueue()` | `public void RemoveFromQueue(int queueIndex)` | public |
| `ItemManufacturing.CanManufacture()` | `public ManufactureFailureReason CanManufacture(ManufacturableItem item)` | public |

### Injection Strategy

| Target | Harmony Type | Purpose |
|--------|-------------|---------|
| `ItemManufacturing.EnqueueItem()` | Prefix | Validate permission before queueing |
| `ItemManufacturing.RemoveFromQueue()` | Prefix | Validate permission before removing |

---

## 3. Base Management

```csharp
// Namespace: PhoenixPoint.Geoscape.Entities
```

### Key Methods on `GeoPhoenixBase`

| Method | Signature | Access |
|--------|-----------|--------|
| `ConstructFacility()` | `public GeoPhoenixFacility ConstructFacility(PhoenixFacilityDef def, Vector2Int pos, PhoenixBaseLayoutRotation rot)` | public |
| `RemoveFacility()` | `public void RemoveFacility(GeoPhoenixFacility facility, bool scrap)` | public |
| `CanBuildFacility()` | `public bool CanBuildFacility(PhoenixFacilityDef def)` | public |
| `CanRepairFacility()` | `public bool CanRepairFacility(GeoPhoenixFacility facility)` | public |
| `RepairFacility()` | `public void RepairFacility(GeoPhoenixFacility facility)` | public |

### Events on `GeoPhoenixBase`
- `OnFacilityStateChanged` — facility state transitions
- `OnFacilitySoldierSlotChanged` — personnel assignment
- `OnUnderpoweredChanged`
- `BaseDiscoveredAlienBase` / `BaseDiscoveredPromotedAlienBase`

### Injection Strategy

| Target | Harmony Type | Purpose |
|--------|-------------|---------|
| `GeoPhoenixBase.ConstructFacility()` | Prefix | Validate permission before construction |
| `GeoPhoenixBase.RemoveFacility()` | Prefix | Validate permission before removal |
| `GeoPhoenixBase.RepairFacility()` | Prefix | Validate permission before repair |

---

## 4. Aircraft Management

```csharp
// Namespace: PhoenixPoint.Geoscape.Entities
```

### Key Methods on `GeoVehicle`

| Category | Method | Signature |
|----------|--------|-----------|
| Equipment | `AddEquipment()` | `public void AddEquipment(GeoVehicleEquipmentDef equipmentDef)` |
| Equipment | `RemoveEquipment()` | `public void RemoveEquipment(GeoVehicleEquipment equipment)` |
| Equipment | `ClearEquipments()` | `public void ClearEquipments()` |
| Equipment | `ReplaceEquipments()` | `public void ReplaceEquipments(List<...> weapons, List<...> modules)` |
| Equipment | `UseLoadout()` | `public void UseLoadout(GeoVehicleLoadoutDef loadout)` |
| Crew | `AddCharacter()` | `public void AddCharacter(GeoCharacter character)` |
| Crew | `RemoveCharacter()` | `public void RemoveCharacter(GeoCharacter character)` |
| Travel | `StartTravel()` | `public void StartTravel(List<GeoSite> path)` |
| Travel | `TravelTo()` | `public bool TravelTo(GeoSite site)` |
| Repair | `RepairAircraftHp()` | `public void RepairAircraftHp(int points)` |
| Repair | `ScheduleRepair()` | `public bool ScheduleRepair(TimeUnit, int)` |

### Injection Strategy

| Target | Harmony Type | Purpose |
|--------|-------------|---------|
| `GeoVehicle.AddEquipment(def)` | Prefix | Validate permission |
| `GeoVehicle.AddCharacter(GeoCharacter)` | Prefix | Validate permission (soldier assignment) |
| `GeoVehicle.StartTravel(List<GeoSite>)` | Prefix | Validate permission (deployment) |

---

## 5. Soldier Management

```csharp
// Namespace: PhoenixPoint.Geoscape.Entities
```

### Key Methods on `GeoCharacter`

| Method | Signature | Access |
|--------|-----------|--------|
| `SetItems()` | `public void SetItems(IEnumerable<GeoItem> armour, IEnumerable<GeoItem> equipment, IEnumerable<GeoItem> inventory, bool freeReload)` | public |

### Key Methods on `GeoPhoenixFaction`

| Method | Signature | Access |
|--------|-----------|--------|
| `HireNakedRecruit()` | `public void HireNakedRecruit(GeoUnitDescriptor character, IGeoCharacterContainer container)` | public |
| `AddRecruit()` | `public IGeoCharacterContainer AddRecruit(GeoCharacter recruit, IGeoCharacterContainer container)` | public |
| `KillCharacter()` | `public override void KillCharacter(GeoCharacter unit, CharacterDeathReason reason)` | public |

### Injection Strategy

| Target | Harmony Type | Purpose |
|--------|-------------|---------|
| `GeoCharacter.SetItems(...)` | Prefix | Validate permission before equipment change |
| `GeoPhoenixFaction.HireNakedRecruit()` | Prefix | Validate permission (recruitment) |
| `GeoFaction.KillCharacter()` | Prefix | Validate permission (dismissal) |

---

## 6. Campaign State Access

Primary singleton: `GeoLevelController.Instance` → `Instance.PhoenixFaction` (player's `GeoPhoenixFaction`).

```
GeoLevelController.Instance
  └─ PhoenixFaction (GeoPhoenixFaction)
      ├─ Research (Research) — research queue
      ├─ Manufacture (ItemManufacturing) — manufacturing queue
      ├─ ItemStorage — faction inventory
      ├─ Bases — list of GeoPhoenixBase
      ├─ Vehicles — list of GeoVehicle
      ├─ Characters — soldier roster
      └─ Skills — faction-wide skills/perks
```

## Permission System Architecture

Recommended approach: **Host-side permission check via Harmony Prefix on each campaign action.**

```csharp
// Pattern for every campaign permission check:
[HarmonyPrefix]
[HarmonyPatch(typeof(Research), "SetQueued")]
static bool CheckResearchPermission(ResearchDef def, bool manualAdd)
{
    if (!MultiplayerClient.IsConnected) return true; // single player: allow

    ulong clientId = GetCallingClientId(); // via some context
    return PermissionManager.HasPermission(clientId, Permission.ManageResearch);
}
```

### Permissions Enum (proposed)

```csharp
[Flags]
public enum CampaignPermission
{
    None               = 0,
    ControlSoldiers    = 1 << 0,  // Tactical combat: assigned soldiers
    ManageEquipment    = 1 << 1,  // Equip/unequip soldiers & vehicles
    ManageBases        = 1 << 2,  // Build/remove facilities
    ManageResearch     = 1 << 3,  // Queue/cancel research projects
    ManageManufacturing = 1 << 4, // Queue/cancel manufacturing
    ManageRecruitment  = 1 << 5,  // Hire/dismiss soldiers
    ManageAircraft     = 1 << 6,  // Deploy, equip, repair aircraft
    ControlTime        = 1 << 7,  // Pause / change the geoscape clock speed
    ForceEndTurn       = 1 << 8,  // End the tactical player phase for everyone
    FullCommander      = 1 << 9,  // All permissions + override others
}
```

This is the **authoritative permission set** for the mod. Two flags extend beyond the per-subsystem checks above:

- **`ControlTime`** — gates who may pause / change the shared geoscape clock → [08-geoscape-concurrency](08-geoscape-concurrency.md).
- **`ForceEndTurn`** — gates who may force-end the tactical player phase for the whole group → [07-tactical-concurrency](07-tactical-concurrency.md).
