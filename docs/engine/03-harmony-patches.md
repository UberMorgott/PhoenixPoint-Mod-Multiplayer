# Multipleer Harmony Patches (as-built)

> The mod uses **Harmony** to intercept key methods of the original game. All gameplay patches are **Prefix** (run before the original method); returning `false` blocks the original, `true` lets it run. Patch-site research → [research/01-tactical-action-pipeline](../research/01-tactical-action-pipeline.md), [research/03-campaign-layer](../research/03-campaign-layer.md); reusable patch patterns → [research/06-harmony-patterns](../research/06-harmony-patterns.md).
>
> Lifecycle: `MultipleerMain.OnModEnabled()` calls `harmony.PatchAll(Assembly.GetExecutingAssembly())` — Harmony scans the assembly, finds every class with a `[HarmonyPatch]` attribute, and applies the patches automatically.

## Patch Table

| ID | Class | Target type | Target method | Type | Purpose |
|----|-------|-------------|---------------|------|---------|
| P0 | `ActivateAbilityPatch` | `TacticalViewState` | `ActivateAbility(TacticalAbility, object)` | Prefix | Universal intercept of all tactical actions: serialize and send to host, block client-side execution |
| P1 | `RequestEndTurnPatch` | `TacticalFaction` | `RequestEndTurn()` | Prefix | Client sends `EndTurnRequest` to host without ending the turn locally |
| P2 | `FireWeaponPatch` | `TacticalLevelController` | `FireWeaponAtTargetCrt()` | Prefix | Fully blocks weapon firing on the client — only the host executes weapons |
| P3 | `InventoryActionPatch` | `UIStateInventory` | `AttemptMoveItems()` | Prefix | Inventory item moves: client sends a request to host |
| C1 | `ResearchPermissionPatch` | `Research` | `SetQueued(ResearchDef, bool)` | Prefix | Blocks queueing research on the client |
| C2 | `ManufacturingPermissionPatch` | `ItemManufacturing` | `EnqueueItem()` | Prefix | Blocks manufacturing on the client |
| C3 | `BaseConstructionPermissionPatch` | `GeoPhoenixBase` | `ConstructFacility()` | Prefix | Blocks facility construction on the client |
| C4 | `VehicleEquipmentPermissionPatch` | `GeoVehicle` | `AddEquipment(GeoVehicleEquipmentDef)` | Prefix | Blocks vehicle equipping on the client |
| C5 | `SoldierEquipmentPermissionPatch` | `GeoCharacter` | `SetItems()` | Prefix | Blocks soldier equipping on the client |

## Runtime Type-Resolution Pattern

Phoenix Point uses obfuscated/internal types unavailable at compile time. Every patch applies a three-step pattern:

1. **`Prepare()`** — called by Harmony before applying the patch. Via `AccessTools.TypeByName("full.type.name")` it obtains the target class `Type`, then via `AccessTools.Method(type, "methodName", argTypes)` the `MethodBase`. If resolution fails it returns `false`, and the patch is not applied.
2. **`TargetMethod()`** — returns the `MethodBase` cached in `Prepare`. Harmony uses it as the patch target.
3. **`Prefix()`** — the intercept logic. Takes parameters matching the target method signature (by name and type).

At the end of `CampaignPatches.cs` there are **runtime stubs** — empty prototype classes (`ResearchDef`, `GeoVehicle`, etc.) that Harmony resolves at runtime from the game assembly. They exist only to compile; the real types are supplied by `AccessTools.TypeByName`.

## P0 — ActivateAbilityPatch: Universal Action Intercept

**Target:** `PhoenixPoint.Tactical.View.TacticalViewState.ActivateAbility(TacticalAbility, object target)`

The most complex patch in the mod. It intercepts **any** tactical action: firing, movement, reload, overwatch, ability use. (This is the "universal chokepoint" identified in [research/01-tactical-action-pipeline](../research/01-tactical-action-pipeline.md).)

### Identifying the Actor, Ability & Target

Because `ActivateAbility` takes an `object target`, and `TacticalAbility` is a game type unavailable at compile time, the patch uses **reflection** to extract data:

- **Actor:** `ability.GetType().GetProperty("TacticalActorBase")` → `actorBase`, then `actorBase.GetType().GetProperty("GeoUnitId")` → `geoUnit`, field `_id` → `actorGeoId`.
- **Ability def:** `ability.GetType().GetProperty("Def")` → `def`, property `Guid` → `abilityDefId`.
- **Action classification:** `ResolveActionType(ability)` — matches the ability type name via `string.Contains`: `"Shoot"` → `Shoot`, `"Move"` → `Move`, `"Reload"` → `Reload`, `"Overwatch"` → `Overwatch`. Everything else → `UseAbility`.

### Serializing the Target

`SerializeTarget(object target)` handles the generic target object, extracting:

- **Actor** — via reflection on the target object's `Actor` field/property → `GeoUnitId._id`.
- **PositionToApply** — via reflection on the `PositionToApply` field/property → `Vector3`.

The result is packed via `Serialization.NetworkSerializer.SerializeTargetData(targetGeoId, x, y, z, 0, null)` into a `byte[]`.

### Blocking Client-Side Execution

After successful serialization the patch builds a `TacticalActionMessage`, sends it via `engine.SendTacticalAction(action)`, and returns **`false`** — the original method on the client **does not run**. The game engine never sees the `ActivateAbility` call on the client; the action executes only on the host after the message is received and verified.

On a reflection error the patch logs the error and returns `true` (lets the original run) — a fallback so the game does not break.

## P1 — RequestEndTurnPatch

**Target:** `TacticalFaction.RequestEndTurn()`

The client intercepts end-of-turn: it builds a `NetworkMessage(PacketType.EndTurnRequest)`, sends it to the host via `engine.SendToHost(msg)`, and returns `false`. The host receives the message and triggers end-of-turn itself. The full multi-player turn-end coordination (ready-counter + host force-end) → [research/07-tactical-concurrency](../research/07-tactical-concurrency.md).

## P2 — FireWeaponPatch

**Target:** `TacticalLevelController.FireWeaponAtTargetCrt()`

The strictest patch: the client always returns `false`, fully blocking weapon execution. The host lets the original run (`return true`). The patch performs no extra logic — it is only a gate. This is the host-only execution point for the projectile/damage RNG flagged in [research/02-rng-analysis](../research/02-rng-analysis.md).

## P3 — InventoryActionPatch

**Target:** `UIStateInventory.AttemptMoveItems()`

The client sends a `NetworkMessage(PacketType.TacticalActionRequest)` to the host and blocks the local item move. The host performs the move with the original method.

## C1–C5 — Campaign Patches: Permission-Check Pattern

All five campaign patches follow the same structure:

```
if engine == null || !engine.IsActive → return true   (not multiplayer — allow)
if !engine.IsHost → return false                       (client — block)
return true                                            (host — allow)
```

Differences:
- **C1 (ResearchPermissionPatch)** — additionally checks `manualAdd`: it blocks only manual queueing, not automatic queueing (e.g. from completed prerequisite research).
- **C2–C5** use `CampaignPermissionHelper.Check(CampaignPermission.X)`, which contains the same logic but is extensible for future role-based permissions.

## Helper Class CampaignPermissionHelper

```csharp
internal static class CampaignPermissionHelper
{
    public static bool Check(CampaignPermission required)
    {
        var engine = NetworkEngine.Instance;
        if (engine == null || !engine.IsActive) return true;
        if (!engine.IsHost) return false;
        return true;
    }
}
```

The `CampaignPermission` parameter does not yet affect the logic — it is reserved for the future role system (which players exactly may build, manufacture, etc.). The full permission set + enforcement design → [research/03-campaign-layer](../research/03-campaign-layer.md), [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md) §4.

---

## Connection-Menu UI Injection (design)

> Native-styled menu for entering multiplayer. Designed; the UI-binding specifics need the SDK → [specs/03-open-questions-sdk](../specs/03-open-questions-sdk.md). This is the planned Harmony injection into PP's menu UI (distinct from the gameplay patches above).

### Main-Menu Injection

- Add a **"Сетевая игра" / "Network Game"** button above **"Play"** in the main menu.
- Implemented via **Harmony** injection into PP's main-menu UI state.
- **Native look for free:** clone an existing menu-button prefab → it inherits the game's fonts, styling, hover/click behavior.
- No custom skinning — reuse game UI so it is indistinguishable from vanilla.

### "Network Game" Screen

- Cloned style of the existing **Mods / Options** screen.
- Two entries:
  - `Create Game` → opens the **Lobby (host)** immediately (no save-picker yet — the picker opens after `Play`, see [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md) §2).
  - `Join Game` → opens the **Join** screen.

### Join Screen

- Three inputs, all feeding the same `ITransport` ([02-transport-layer](02-transport-layer.md)):
  - `Steam: Friends` → Steam overlay invite (a later phase).
  - **Single text input** with **autodetect**:
    - Matches `IP[:port]` / hostname → route to the **DirectIP** transport.
    - Otherwise → treat as a **Steam connection code**.
  - `Connect` button → joins the **Lobby (client)**.
- One box, auto-routing → the user pastes either an IP or a code, and the mod figures out which.

### Navigation

- All screens go through PP's native UI state stack (push/pop), consistent with the vanilla menu flow.
- Back/escape behaves like other native sub-screens.

### SDK Unknowns

- The main-menu UI state class to patch (e.g. `UIStateMainMenu`?), the button prefab to clone, and the UI state-stack push/pop API are all unverified without the SDK → [specs/03-open-questions-sdk](../specs/03-open-questions-sdk.md).
