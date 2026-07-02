> **⚠ ANCESTOR VISION (2026-06-08).** The high-level co-op concept below still holds, but this
> document's ARCHITECTURE is SUPERSEDED by the unified `0x67` sync backbone + sync canon
> (see `../../CLAUDE.md` "Multiplayer sync canon" + `../COOP-SYNC-ROADMAP.md`). Known-stale patch
> names herein: the real hooks are `Research.AddResearchToQueue` and
> `ItemManufacturing.ManufactureItem`. Read as design lineage, not as-built.

# Multipleer — Cooperative Multiplayer Mod Design

> This is the action-sync core design. Session/lobby/identity/persistence design → [02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md); SDK unknowns backlog → [03-open-questions-sdk](03-open-questions-sdk.md). Decompiled source-dive findings live under [../research/](../research/); as-built implementation under [../engine/](../engine/).

## 0. Project Goal & Co-op Concept

- A multiplayer mod for Phoenix Point built on the official **SDK** + **Harmony** patches.
- **Not** a traditional turn-based multiplayer where players wait for each other.
- A **cooperative campaign**: multiple players share one campaign, each controlling different aspects of the same faction.

### Co-op Concept

- Different players control different soldiers.
- Different players are granted permissions over distinct subsystems: soldier management, equipment, base management, research, manufacturing, recruitment, aircraft management, tactical combat control.
- The host assigns permissions + soldier ownership through a management UI.

### Ownership + Permission Model (overview)

- **Ownership** = which soldiers a given player may command (tactical + roster).
- **Permission** = which campaign subsystems a player may operate.
- Both are **dynamically configurable** by the host at runtime.
- Full design → [research/03-campaign-layer](../research/03-campaign-layer.md) (permission set + enforcement) and [02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md) §3–§4 (ownership map, persistence, identity).

## 1. Architecture Overview

### Authoritative Host Model

- The host is the **only source of truth**: it executes all game logic, runs all random events, calculates all combat results, controls AI, generates missions, and owns the campaign state.
- Clients send player actions to the host, receive validated actions/results, and reproduce the game state locally.
- **Design stance: avoid lockstep simulation.** The objective is to minimize desync caused by RNG + hidden game systems. The authoritative model is chosen specifically to keep RNG + AI + event generation on a single deterministic node (the host). Desync analysis → [research/02-rng-analysis](../research/02-rng-analysis.md); the action pipeline is detailed in §3.3.

```
┌─────────────────────────────────────────────────────────┐
│                     HOST (Authoritative)                 │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────┐  │
│  │ Game State   │  │ Action       │  │ Permission     │  │
│  │ (Full Game)  │  │ Validator    │  │ Manager        │  │
│  └──────┬───────┘  └──────┬───────┘  └────────────────┘  │
│         │                 │                               │
│         ▼                 ▼                               │
│  ┌──────────────────────────────────────────────────┐    │
│  │              Network Server                       │    │
│  │  (Facepunch.Steamworks P2P — SteamNetworking)     │    │
│  └──────────────────────────────────────────────────┘    │
└─────────────────────────┬───────────────────────────────┘
                          │
                          │ P2P Packets (Steam relay/NAT)
                          │
┌─────────────────────────┴───────────────────────────────┐
│                    CLIENT(S)                             │
│  ┌────────────────┐  ┌────────────────────────────────┐ │
│  │ Network Client │  │ Action Sender                  │ │
│  └───────┬────────┘  │ (Intercepts input → serializes │ │
│          │           │  → sends to host)               │ │
│          ▼           └────────────────────────────────┘ │
│  ┌──────────────────────────────────────────────────┐    │
│  │        Local Game State (Read-Only + Results)     │   │
│  │  Reproduces host-authorized actions locally       │   │
│  └──────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────┘
```

### Data Flow per Action

```
Client Action (click shoot/move/etc.)
  │
  ├─ Harmony Prefix intercepts call
  ├─ Serializes action: (actorGeoTacId, abilityDefId, targetData)
  ├─ Sends to host via SteamNetworking.SendP2PPacket()
  │
  ▼
Host receives packet
  │
  ├─ Deserializes action
  ├─ Validates: does client control this actor?
  ├─ Validates: is this action legal in current game state?
  ├─ Permission check: client has permission for this action type
  ├─ EXECUTES original game logic (ability.Activate(target))
  │
  ├─ Broadcasts RESULT to all clients:
  │   (actionId, success/fail, resultData — final position, damage dealt, etc.)
  │
  ▼
Client receives result
  │
  ├─ Applies result to local game state (sets final position, applies damage)
  └─ Visual feedback (animation plays locally)
```

### What Is NOT Synced (Local-Only)

- Camera position, movement, rotation
- Unit selection / cursor position
- UI navigation / open panels
- Tooltip state / hover targets
- Animation timing (results are applied, animations play locally)
- Audio

---

## 2. Class Diagrams

### 2.1 Network Layer

```
┌─────────────────────────────────────────────────────────┐
│ NetworkManager                                            │
│  (MonoBehaviour — attached to root Game object)          │
├─────────────────────────────────────────────────────────┤
│ - _hostSteamId: ulong                                     │
│ - _isHost: bool                                           │
│ - _localSteamId: ulong                                    │
│ - _clients: Dictionary<ulong, ClientConnection>           │
│ - _pendingActions: Queue<NetworkedAction>                 │
├─────────────────────────────────────────────────────────┤
│ + Initialize(isHost: bool): void                          │
│ + Host_CreateLobby(): void                                │
│ + Client_JoinLobby(lobbyId: ulong): void                  │
│ + SendToClient(clientId: ulong, packet: byte[]): void     │
│ + SendToHost(packet: byte[]): void                        │
│ + BroadcastToAll(packet: byte[]): void                    │
│ + Update(): void  (call every frame)                      │
│ - ProcessIncomingPackets(): void                          │
│ - OnP2PPacket(steamId, data): void                        │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│ ClientConnection                                          │
├─────────────────────────────────────────────────────────┤
│ + SteamId: ulong                                          │
│ + PlayerName: string                                      │
│ + AssignedSoldiers: List<GeoTacUnitId>                    │
│ + Permissions: CampaignPermission                         │
│ + IsReady: bool                                           │
│ + LatencyMs: int                                          │
└─────────────────────────────────────────────────────────┘
```

### 2.2 Action Serialization

```
┌─────────────────────────────────────────────────────────┐
│ NetworkedAction (struct)                                  │
├─────────────────────────────────────────────────────────┤
│ + ActionId: Guid          — unique per action            │
│ + ActionType: ActionType  — enum (Move, Shoot, etc.)     │
│ + ClientSteamId: ulong    — who sent it                  │
│ + ActorGeoId: GeoTacUnitId — which unit                  │
│ + AbilityDefId: string    — ability def GUID             │
│ + TargetData: byte[]      — serialized target params     │
│ + Timestamp: long         — for ordering                 │
├─────────────────────────────────────────────────────────┤
│ + Serialize(): byte[]                                     │
│ + Deserialize(data: byte[]): NetworkedAction              │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│ ActionResult (struct)                                    │
├─────────────────────────────────────────────────────────┤
│ + ActionId: Guid          — matches request              │
│ + Success: bool            — was action valid/executed   │
│ + ErrorReason: string      — if failed, why              │
│ + ResultData: byte[]       — serialized action result    │
│  (final position, damage applied, items changed, etc.)   │
└─────────────────────────────────────────────────────────┘

enum ActionType : byte
{
    Move,
    Shoot,
    Reload,
    UseAbility,
    InventoryTransfer,
    EndTurn
}

enum CampaignActionType : byte
{
    StartResearch,
    QueueManufacturing,
    ConstructFacility,
    RemoveFacility,
    EquipSoldier,
    DeployAircraft,
    HireRecruit,
    AssignSoldier
}
```

### 2.3 Permission System

```
┌─────────────────────────────────────────────────────────┐
│ PermissionManager                                         │
├─────────────────────────────────────────────────────────┤
│ - _assignments: Dictionary<ulong, PlayerAssignment>       │
├─────────────────────────────────────────────────────────┤
│ + HasPermission(steamId, permission): bool                │
│ + AssignSoldier(steamId, soldierId): void                 │
│ + UnassignSoldier(steamId, soldierId): void               │
│ + SetPermission(steamId, permission, granted): void       │
│ + GetAssignment(steamId): PlayerAssignment                │
│ + GetManagementUI(): UIModuleMultiplayerManagement        │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│ PlayerAssignment                                          │
├─────────────────────────────────────────────────────────┤
│ + SteamId: ulong                                          │
│ + PlayerName: string                                      │
│ + OwnedSoldierIds: List<GeoTacUnitId>                     │
│ + Permissions: CampaignPermission                         │
└─────────────────────────────────────────────────────────┘

[Flags]
enum CampaignPermission
{
    None               = 0,
    ControlSoldiers    = 1 << 0,  // command assigned soldiers (tactical)
    ManageEquipment    = 1 << 1,
    ManageBases        = 1 << 2,
    ManageResearch     = 1 << 3,
    ManageManufacturing = 1 << 4,
    ManageRecruitment  = 1 << 5,
    ManageAircraft     = 1 << 6,
    ControlTime        = 1 << 7,  // pause / change geoscape clock speed
    ForceEndTurn       = 1 << 8,  // end the tactical player phase for everyone
    FullCommander      = 1 << 9   // all permissions + override
}
```

> The expanded flags `ControlTime` and `ForceEndTurn` gate the geoscape shared clock ([research/08-geoscape-concurrency](../research/08-geoscape-concurrency.md)) and the tactical turn-end ready-gate ([research/07-tactical-concurrency](../research/07-tactical-concurrency.md)) respectively. The authoritative flag set + enforcement (Harmony prefix per subsystem) lives in [research/03-campaign-layer](../research/03-campaign-layer.md); the host-side assignment UI + cross-session persistence in [02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md) §4.

### 2.4 Core Harmony Patch Points

```
┌─────────────────────────────────────────────────────────┐
│ Harmony Patches (organized by layer)                     │
├─────────────────────────────────────────────────────────┤
│ TACTICAL                                                 │
│  ├─ [P0] TacticalViewState.ActivateAbility — PREFIX     │
│  │     Universal action intercept: (ability, target)    │
│  │     → serialize → send to host → await validation    │
│  │                                                      │
│  ├─ [P1] TacticalFaction.RequestEndTurn — PREFIX        │
│  │     → send end-turn to host, don't end locally       │
│  │                                                      │
│  ├─ [P2] TacticalLevelController.FireWeaponAtTargetCrt  │
│  │     PREFIX: host-only — execute actual damage        │
│  │     POSTFIX: broadcast damage results                │
│  │                                                      │
│  ├─ [P3] UIStateInventory.AttemptMoveItems — PREFIX     │
│  │     → send inventory action to host                  │
│  │                                                      │
│  ├─ [P4] MoveAbility.Move — PREFIX                      │
│  │     → send move path to host                         │
│  │                                                      │
│  └─ [P5] TacticalLevelController.NextTurnCrt — POSTFIX  │
│        → broadcast turn state to clients                │
│                                                         │
│ CAMPAIGN                                                 │
│  ├─ [C1] Research.SetQueued — PREFIX (permission)       │
│  ├─ [C2] ItemManufacturing.EnqueueItem — PREFIX          │
│  ├─ [C3] GeoPhoenixBase.ConstructFacility — PREFIX       │
│  ├─ [C4] GeoVehicle.AddEquipment — PREFIX                │
│  ├─ [C5] GeoCharacter.SetItems — PREFIX                  │
│  ├─ [C6] GeoPhoenixFaction.HireNakedRecruit — PREFIX    │
│  └─ [C7] GeoVehicle.StartTravel — PREFIX                 │
└─────────────────────────────────────────────────────────┘
```

---

## 3. Network Protocol

### 3.1 Message Types

```
enum MessageType : byte
{
    // Connection
    ConnectionRequest,       // client → host: "I want to join"
    ConnectionAccepted,      // host → client: "you're in" + initial state
    ConnectionRejected,      // host → client: "no" + reason
    ClientDisconnected,      // client → host (or host → all)
    HostDisconnected,        // host → all

    // Session
    InitialGameState,        // host → client: full campaign state on join
    GameStateDelta,          // host → client: incremental state update

    // Tactical Actions
    TacticalActionRequest,   // client → host: "I want to do X"
    TacticalActionResult,    // host → client: "X completed/failed" + result

    // Campaign Actions
    CampaignActionRequest,   // client → host: campaign action
    CampaignActionResult,    // host → client: result

    // Management
    PermissionUpdate,        // host → client: permissions changed
    SoldierAssignment,       // host → client: soldier ownership changed
    ChatMessage,             // any → any
    Ping, Pong               // latency measurement
}
```

### 3.2 Tactical Action Protocol (Detailed)

```
CLIENT →
  MessageType: TacticalActionRequest
  ActionId: Guid
  ActorGeoId: int (GeoTacUnitId)
  AbilityDefId: string (GUID)
  TargetData: byte[] (serialized TacticalAbilityTarget)
  Timestamp: long

HOST response →
  MessageType: TacticalActionResult
  ActionId: Guid (echo back)
  Success: bool
  ResultData: byte[] (only if success — serialized post-action state)
    For Move: final GridPos, AP remaining
    For Shoot: targets hit, damage dealt, kills, status changes
    For Reload: ammo counts
    For Ability: which ability, targets, effects
  ErrorReason: string (only if !success)
```

### 3.3 State Synchronization Strategy

**Action pipeline (the core loop):**

```
Client Action
  → Network Message
  → Host Validation
  → Host Executes Original Game Logic
  → Host Broadcasts Action Result
  → Clients Reproduce Result
```

- "Host executes original game logic" = let the unmodified game code run on the host, capture the results, and broadcast — this avoids reimplementing combat math.
- Clients **reproduce** the broadcast outcome; they do **not** recompute it — keeping RNG single-sourced ([research/02-rng-analysis](../research/02-rng-analysis.md)).
- A full-state snapshot is the fallback for divergence ([research/04-serialization](../research/04-serialization.md)); the same snapshot path is reused for reconnect resync ([research/09-disconnect-reconnect](../research/09-disconnect-reconnect.md)).
- Tactical conflict resolution (same-tile races, destination reservation, turn-end gate) → [research/07-tactical-concurrency](../research/07-tactical-concurrency.md).

**Upon client join (during tactical mission):**
1. Host pauses action processing
2. Host serializes full `TacLevelSavegame.Data` → sends as `InitialGameState`
3. Client deserializes and loads into local level
4. Client sends `Ready` → host resumes

**Delta updates (during gameplay):**
- After each host-executed action, host broadcasts only the `ActionResult`
- Client applies `ResultData` directly (sets position, applies damage, etc.)
- No RNG replay on clients — results are authoritative

**Periodic snapshots (optional):**
- Host sends full tactical state every N turns as a sync-check
- Clients verify their state matches; request resync if mismatch detected

---

## 4. Harmony Patch Implementation Plan

### 4.1 Tactical Layer Patches

#### Patch P0: `TacticalViewState.ActivateAbility` (Universal Chokepoint)

```csharp
[HarmonyPrefix]
[HarmonyPatch(typeof(TacticalViewState), "ActivateAbility",
    new Type[] { typeof(TacticalAbility), typeof(TacticalAbilityTarget) })]
static bool OnActivateAbility(TacticalViewState __instance,
    TacticalAbility ability, TacticalAbilityTarget target)
{
    if (!MultiplayerManager.IsActive) return true; // single player: pass through
    if (MultiplayerManager.IsHost) return true;     // host: execute directly

    // Client: send to host, block local execution
    var action = new NetworkedAction
    {
        ActionId = Guid.NewGuid(),
        ActionType = ResolveActionType(ability),
        ActorGeoId = ability.TacticalActorBase.GeoUnitId,
        AbilityDefId = ability.Def.Guid,
        TargetData = SerializeTarget(target)
    };

    NetworkManager.Instance.SendToHost(action.Serialize());

    // Return false to PREVENT local execution — wait for host response
    NetworkManager.Instance.PendingActions[action.ActionId] = __instance;
    return false; // block original — will be called by host response
}
```

```csharp
// On host response:
[HarmonyPostfix]
[HarmonyPatch(typeof(TacticalViewState), "ActivateAbility", ...)]
static void OnActivateAbilityPost(TacticalViewState __instance,
    TacticalAbility ability, TacticalAbilityTarget target)
{
    // Client receiving result — no-op here, handled by network layer
}
```

#### Patch P1: `TacticalFaction.RequestEndTurn`

```csharp
[HarmonyPrefix]
[HarmonyPatch(typeof(TacticalFaction), "RequestEndTurn")]
static bool OnRequestEndTurn()
{
    if (!MultiplayerManager.IsActive || MultiplayerManager.IsHost)
        return true;

    // Client: send end-turn request to host
    NetworkManager.Instance.SendToHost(EndTurnRequest());
    return false; // host will trigger end-turn when all clients are ready
}
```

#### Patch P2: `TacticalLevelController.FireWeaponAtTargetCrt` (Host-side)

```csharp
[HarmonyPrefix]
[HarmonyPatch(typeof(TacticalLevelController), "FireWeaponAtTargetCrt")]
static bool OnFireWeapon(TacticalLevelController __instance)
{
    if (!MultiplayerManager.IsHost)
    {
        // Clients don't execute weapons — host broadcasts results
        return false;
    }
    return true; // host: execute original
}

// After execution, broadcast:
[HarmonyPostfix]
[HarmonyPatch(typeof(TacticalLevelController), "FireWeaponAtTargetCrt")]
static void OnFireWeaponPost(TacticalLevelController __instance,
    ref IEnumerator __result)
{
    if (!MultiplayerManager.IsHost) return;
    // Wrap the coroutine to broadcast results after completion
    __result = BroadcastAfterCoroutine(__result, __instance);
}
```

### 4.2 Campaign Layer Patches

All campaign patches follow the same pattern — check permission, then allow/deny:

```csharp
// Generic campaign permission check pattern:
[HarmonyPrefix]
[HarmonyPatch(typeof(Research), "SetQueued")]
static bool OnSetQueued(ResearchDef def, bool manualAdd)
{
    if (!MultiplayerManager.IsActive) return true; // single player
    if (MultiplayerManager.IsHost) return true;     // host: check permission

    // Client: forward to host
    NetworkManager.Instance.SendToHost(
        new CampaignActionRequest { Type = CampaignActionType.StartResearch, ... });
    return false;
}

// On host: execute if client has ManageResearch permission
[HarmonyPrefix]
[HarmonyPatch(typeof(Research), "SetQueued")]
static bool OnSetQueued_HostCheck(Research __instance, ResearchDef def, bool manualAdd)
{
    if (!MultiplayerManager.IsHost) return true;
    if (!manualAdd) return true; // game-initiated changes always pass

    ulong callerSteamId = NetworkManager.Instance.CurrentCallerSteamId;
    if (!PermissionManager.HasPermission(callerSteamId, CampaignPermission.ManageResearch))
    {
        NetworkManager.Instance.SendToClient(callerSteamId,
            new ActionResult { Success = false, ErrorReason = "No Research permission" });
        return false; // block
    }
    return true; // allow
}
```

---

## 5. Risk Assessment

| Risk | Severity | Likelihood | Mitigation |
|------|----------|------------|------------|
| **RNG Desync** — Clients and host get different RNG results | High | High | **State-sync architecture**: don't replay RNG on clients; send results |
| **Hidden Game State** — Game systems modify state that the mod doesn't know about | High | Medium | Extensive testing; use save/load cycle as ground truth; periodic full state sync |
| **Timing Issues** — Coroutine-based game loop conflicts with network latency | Medium | High | Queue actions; use turn-based action ordering; host's timing is authoritative |
| **ModSDK Limitations** — Required types/methods not in public SDK | Medium | High | Use `TargetMethod()` dynamic resolution pattern; verify against runtime assembly |
| **Steam P2P Reliability** — Packet loss or latency | Low | Medium | Use reliable P2P for action requests; implement timeout + retry |
| **Permission Bypass** — Client sends forged actions | High | Low | Host validates ALL actions against current permissions AND game state |
| **Save Compatibility** — Mod saves incompatible with vanilla | Medium | Medium | Store multiplayer metadata in `ModData` dictionary (already supported by save system) |
| **AI Turn Delay** — Clients wait while host computes AI | Low | High | Show "Enemy Turn" indicator; AI runs only on host; clients receive simplified updates |
| **TFTV/Officer Compatibility** — Other mods patch the same methods | Medium | Medium | Use Harmony Patch priority; test compatibility; document conflicts |
| **Combat Spread/Physics** — Physics-based hit system (raycasts) can't be deterministic | High | High | **Key desync risk**. Mitigation: host runs physics, broadcasts bullet hit results (not projectile sim) |

---

## 6. Desynchronization Risk Assessment

### High Risk Systems

| System | Risk Level | Why |
|--------|-----------|-----|
| **Projectile Physics** (Weapon.cs, ProjectileLogic.cs) | **CRITICAL** | Uses Unity engine physics + UnityEngine.Random. Cannot be deterministic across machines. Must run host-only and broadcast hit results. |
| **AI Decision Making** (AIFaction.cs) | **HIGH** | Uses WeightedRandomElement with no seedable RNG. AI pathfinding depends on Unity navmesh. |
| **Damage Range Rolling** (DamageEffectDef.cs) | **HIGH** | Uses Random.Range(MinDamage, MaxDamage). Must be host-only. |
| **Armor Shred** (DamagePayload.cs) | **MEDIUM** | Random shred probability check. Host-only. |
| **Fumble Checks** (TacticalAbility.cs) | **MEDIUM** | Uses UnityEngine.Random. Host-only. |
| **Weapon Malfunction** (Weapon.cs) | **MEDIUM** | Random malfunction check. Host-only. |

### Low Risk (No Randomness)

| System | Why |
|--------|-----|
| **Movement** | Pathfinding is deterministic given start/end positions and navmesh (same navmesh required on client) |
| **Inventory Transfers** | No randomness — pure state changes |
| **Ability Application** (non-damage) | Status effects, buffs/debuffs — deterministic |
| **Research Queueing** | No randomness |
| **Manufacturing** | No randomness |
| **Base Construction** | No randomness |
| **End Turn** | No randomness |

### Navmesh Consideration

Tactical maps use Unity navmeshes (`TacticalNav`). For movement to be reproducible on clients, the navmesh must be identical. Since the level geometry is loaded from the same mission data, this should be the case — but destruction (voxel-based) changes the navmesh during play. Clients must receive destruction events or use the host's navmesh for movement validation.

---

## 7. Recommended Implementation Order

```
Phase 0: Foundation (Week 1-2)
├── Project setup (Multipleer .csproj, ModSDK references, meta.json)
├── NetworkManager stub (initialization, packet send/recv)
├── Steam P2P connection test (two game instances can exchange packets)
└── Basic message types and serialization

Phase 1: Lobby + Connection (Week 3-4)
├── Lobby creation/joining UI (SteamMatchmaking)
├── Permission system (data model + host assigns permissions)
├── Connection handshake (request → accept → initial game state)
└── Client disconnect handling

Phase 2: Tactical Combat — Actions (Week 5-8)
├── P0: TacticalViewState.ActivateAbility — Prefix intercept
├── Action serialization (move, shoot, ability, reload)
├── Host action validator + executor
├── Host result broadcaster
├── Client result applier
└── First test: two clients, one soldier moves

Phase 3: Tactical Combat — Turn System (Week 9-10)
├── P1: RequestEndTurn — client sync
├── Multi-client end-turn coordination (all clients ready → next turn)
├── Turn state broadcasting
└── Full tactical loop test

Phase 4: Tactical Combat — Full (Week 11-14)
├── P2: FireWeaponAtTargetCrt — host-only execution
├── Damage broadcasting
├── Physics result serialization
├── Kill/death synchronization
├── Overwatch/reaction fire handling
├── Multi-client simultaneous action support
└── Full tactical mission test

Phase 5: Campaign Layer (Week 15-18)
├── Campaign permission checks (C1-C7)
├── Research sync
├── Manufacturing sync
├── Base management sync
├── Equipment sync
├── Client campaign state reproduction
└── Full campaign test

Phase 6: Management UI (Week 19-20)
├── Permissions management panel (host)
├── Soldier assignment UI
├── Connection status overlay
├── Player list and latency display
└── Chat system

Phase 7: Polish (Week 21-24)
├── Reconnection support (state snapshot on join)
├── Desync detection + auto-resync
├── Steam friend invites
├── Performance optimization
├── Compatibility testing with TFTV/Officer
└── Beta testing
```

---

## 8. Minimal Proof-of-Concept Roadmap

A working PoC that can be demonstrated and tested should have:

### PoC Goals
1. Two Steam users connect (lobby + P2P)
2. Host starts a tactical mission
3. Client can move ONE assigned soldier
4. Host validates and broadcasts the move
5. Client sees the result
6. Turn ends cleanly

### PoC Implementation Steps

| Step | What | Key File(s) |
|------|------|-------------|
| 1 | Project scaffolding | `Multipleer.csproj`, `meta.json` |
| 2 | `NetworkManager` with Steam P2P | P2P send/recv, lobby create/join |
| 3 | `MessageSerializer` | `NetworkedAction`, `ActionResult` structs |
| 4 | `TacticalViewState.ActivateAbility` Prefix | Client intercept → serialize → send |
| 5 | Host action receiver + MoveAbility executor | Deserialize → validate ownership → execute |
| 6 | Host result broadcaster | Serialize result → P2P send to all |
| 7 | Client result applier | Deserialize → apply position to local actor |
| 8 | `RequestEndTurn` sync | Client sends end-turn → host processes when all ready |
| 9 | Simple management UI | Host-only: soldier assignment dropdown |

### PoC Minimal Code Estimate

| Component | Files | Lines |
|-----------|-------|-------|
| Project setup + config | 3 | ~60 |
| NetworkManager (P2P + lobby) | 2 | ~300 |
| Message serialization | 2 | ~200 |
| Action interception (Patches) | 3 | ~250 |
| Host validation + execution | 2 | ~200 |
| Client result application | 2 | ~150 |
| Management UI | 2 | ~200 |
| **Total** | **16** | **~1360** |

---

## 9. Key File References

| System | File Path (decompiled) | Lines |
|--------|----------------------|-------|
| Universal action choke | `TacticalViewState.cs` | 289 |
| Ability activation | `TacticalAbility.cs` | 1171 |
| Shoot execution | `ShootAbility.cs` | 152 |
| Move execution | `MoveAbility.cs` | 41 |
| Weapon spread/phys | `Weapon.cs` | 534 |
| Projectile logic | `ProjectileLogic.cs` | — |
| Damage application | `DamagePayload.cs` | 105 |
| Turn management | `TacticalLevelController.cs` | — |
| End turn request | `TacticalFaction.cs` | 383 |
| Inventory sync | `UIStateInventory.cs` | 917 |
| Global RNG | `SharedData.cs` | 70 |
| Steam platform | `PlatformSteam.cs` | — |
| Save/load | `PhoenixSaveManager.cs` | — |
| Serialization engine | `Serializer.cs` | — |
| GeoCharacter state | `GeoCharacter.cs` | — |
| Research queuing | `Research.cs` | — |
| Manufacturing | `ItemManufacturing.cs` | — |
| Base construction | `GeoPhoenixBase.cs` | — |
| Vehicle equipment | `GeoVehicle.cs` | — |

## 10. Abbreviations & Terms

| Term | Meaning |
|------|---------|
| Host | Authoritative game instance. Runs all logic, owns campaign state. |
| Client | Connected player. Sends actions, receives results. |
| GeoTacUnitId | Cross-scene stable soldier ID (int wrapper). |
| P2P | Peer-to-peer (Steam P2P networking). |
| State-Sync | Network architecture: host runs full sim, broadcasts results. |
| Lockstep | Network architecture: all peers run deterministic sim in sync. |
