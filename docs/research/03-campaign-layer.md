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

## Vision / Roadmap — per-player permission MENU (2026-06-13)

- **END GOAL:** a host-driven per-player access-control MENU (the "Player Management" screen, [specs/02](../specs/02-session-lifecycle-and-player-management.md) §4). Host assigns each connected player a granular, per-resource set of rights — not a single global on/off.
- **Granular menu items the host toggles per player:**
  - **Fly across the geoscape map** — gate `GeoVehicle.StartTravel` (→ `ManageAircraft`).
  - **Assign a SPECIFIC aircraft to a specific player** — bind one `GeoVehicle` to one `playerGUID`; only its owner may fly/equip it (per-resource ownership, finer than the flag bits — future extension of the `playerGUID → flags` table to `playerGUID → {flags, ownedVehicleIds}`).
  - **Base management** — `GeoPhoenixBase.ConstructFacility/RemoveFacility/RepairFacility` (→ `ManageBases`).
  - **Squad / roster assignment** — soldier ownership `soldierID → playerGUID` via the Roster surface (§3 of specs/02); distinct panel from permissions.
  - Plus existing flag bits: Research / Manufacturing / Recruitment / Equipment / Control Time / Force End Turn / Full Commander.
- Toggle → `PERMISSION{ playerGUID, flag, value }` broadcast → applied live; persisted host-side in `coop-perms.json` ([specs/02](../specs/02-session-lifecycle-and-player-management.md) §4).
- **Tighten incrementally** toward this menu; do NOT block the working co-op loop on it.

### Interim decision (2026-06-13) — grant EVERYTHING (`FullCommander` default)

- **Now:** both host self-entry AND every joining client default to `FullCommander` → allow-all. No per-resource gating yet; the controlled menu above is deferred.
- **Why "last command wins" is safe under allow-all:** the host-authoritative arbiter ([geoscape-command-sync-design](../superpowers/specs/2026-06-12-geoscape-command-sync-design.md)) serializes all commands on its single-threaded receipt queue, so concurrent client commands never corrupt state — the latest valid command simply wins (consistent with [08-geoscape-concurrency](08-geoscape-concurrency.md) last-writer-wins).
- **Bug it fixes:** joining clients previously got a `ClientInfo` with **no permission entry** (`SessionManager.AddClient` `:124-138` creates `ClientInfo` without flags). `PermissionManager.HasCampaignPermission` `:87-96` returns **`false` when the GUID is absent from `_assignments`** → host rejected ALL client `StartTravel`. Auto-granting `FullCommander` to host + each client populates `_assignments`, and the `FullCommander` override (`:92-93`) makes every `HasCampaignPermission` check pass.
- **Enforcement seam (unchanged):** gating still flows through `PermissionManager.HasCampaignPermission` (FullCommander = allow-all override). When tightening, replace the blanket `FullCommander` grant in the host self-entry / `SessionManager.AddClient` path with menu-driven per-flag (and later per-resource) grants — the gate itself does not change.

> **Doc-path drift note:** [geoscape-command-sync-design](../superpowers/specs/2026-06-12-geoscape-command-sync-design.md) references `docs/research/03-permission-system.md`; the actual file is THIS one (`03-campaign-layer.md`). No `03-permission-system.md` exists.
