# Tactical Action Pipeline — Full Analysis

Source: `decompiled\AssemblyCSharp\Assembly-CSharp\src`

> This is the source-dive that answers the tactical investigation questions: central action entry points, the execution pipeline + Harmony intercept sites, the turn system (player vs enemy phase), stable actor IDs for networking, and mission initialization. It feeds the candidate patch locations used in [specs/01-design](../specs/01-design.md) §2.4/§4. Simultaneous-play / conflict-resolution design built on this pipeline → [07-tactical-concurrency](07-tactical-concurrency.md).

## What Is Synced vs Not (tactical)

- **Synced** (world-changing only): move, shoot, reload, ability use, inventory transfers, interaction with world objects.
- **NOT synced** (local-only): camera, unit selection, cursor position, UI navigation. (Full local-only list → [specs/01-design](../specs/01-design.md) §1.)
- The host **validates every action** before execution: an ownership check (does this player own this soldier?) plus a legality check (is the action valid in the current state?). Validation injects at the entry points below.

## Class Hierarchy

```
ActorComponent (Base.Entities) — ISceneObjectIdTarget
  └─ TacticalActorBase (PhoenixPoint.Tactical.Entities) — base actor
       │  GeoUnitId: GeoTacUnitId (stable cross-scene identifier)
       │  SceneObjectId: SceneObjectId (Guid-based)
       └─ TacticalActor (PhoenixPoint.Tactical.Entities) — player soldier

Ability (Base.Entities.Abilities) — BaseDef, Actor, Activate(), IsEnabled()
  └─ TacticalAbility (PhoenixPoint.Tactical.Entities.Abilities)
       ├─ ShootAbility — weapon attacks, IAttackAbility
       ├─ MoveAbility — movement, IMoveAbility
       ├─ ReloadAbility — reload logic with ammo clips
       ├─ OverwatchAbility — reaction fire
       ├─ InventoryAbility — inventory operations
       └─ (etc.)

TacticalViewState (abstract base)
  ├─ UIStateCharacterSelected — main idle state, context menus
  ├─ UIStateShoot — weapon targeting/firing
  ├─ UIStateAbilitySelected — generic ability targeting
  ├─ UIStateInventory — inventory management
  └─ UIStateCharacterStatus — status screen
```

## Action Execution Flow

### Universal Chokepoint: `TacticalViewState.ActivateAbility()`

```
File: PhoenixPoint.Tactical.View\TacticalViewState.cs:289-307

protected virtual void ActivateAbility(
    TacticalAbility ability,
    TacticalAbilityTarget target,
    StateStackAction stateStackActionToApply = ClearStackAndPush,
    Func<TacticalAbility, bool> onAbilityFinishedExecutionHandler = null)
{
    ability.Activate(target);  // LINE 300 — commits all changes
}
```

**Every ability execution passes through this method.** This is the single best Harmony injection point for network validation.

### Ability Activation Chain

```
UIState (OnSelect/OnConfirm/OnInputEvent)
  └─ ActivateAbility(ability, target)
      └─ TacticalViewState.ActivateAbility()
          └─ TacticalAbility.Activate(parameter)  [TacticalAbility.cs:1171]
              ├─ IncrementUsesThisTurn()
              ├─ ApplyAbilityTraits()
              ├─ ApplyCosts(parameter) — AP/WP deducted here
              └─ PlayAction() or EnqueueAction()
                  └─ Coroutine (Shoot/Move/Reload/etc.)
```

### Per-Action Flow Details

#### Movement
```
UIStateCharacterSelected.OnSelect() → MoveToGridPosition() [line 948]
  └─ ActivateAbility(ActorMoveAbility, moveTarget.ToTarget())
      └─ MoveAbility.Activate(parameter) [MoveAbility.cs:41]
          └─ MoveAbility.Move() coroutine [MoveAbility.cs:123]
              └─ TacticalNav.Navigate() — actual position change
```

#### Shooting
```
UIStateShoot.ConfirmShoot() [UIStateShoot.cs:1337]
  └─ ActivateAbility(_ability, target)  [UIStateShoot.cs:1431]
      └─ ShootAbility.Activate(parameter) [ShootAbility.cs:152]
          └─ ShootAbility.Shoot() coroutine [ShootAbility.cs:268]
              └─ FireWeaponAtTargetCrt() [TacticalLevelController.cs:1539]
                  ← PROJECTILE SPAWNING & DAMAGE APPLICATION
```

#### Inventory
```
UIStateInventory.ExitState() [UIStateInventory.cs:437]
  └─ AttemptMoveItems() [line 758]
      └─ InventoryQuery.SyncItems() — actual item transfer
```

#### End Turn
```
UIStateCharacterSelected.EndTurn() [line 1618]
  └─ Context.View.ViewerFaction.RequestEndTurn()
      └─ TacticalFaction.RequestEndTurn() [TacticalFaction.cs:383]
          └─ sets _endTurnRequested = true
```

### Turn Management Flow

```
TacticalLevelController.NextTurnCrt():
  while (!IsGameOver):
    CurrentFaction.PlayTurnCrt(turnStartAction)
      ├─ Increments TurnNumber
      ├─ Fires StartTurnEvent
      ├─ For AI: runs AIUpdateCrt()
      ├─ For Player: waits for RequestEndTurn() (line 472-484)
      ├─ Sets IsPlayingTurn = false
      ├─ Calls EndTurn() on all owned objects
      ├─ Fires EndTurnEvent
      └─ Fires FactionEndedTurnEvent
    Increments _currentFactionIndex
```

Key events:
- `TacticalFaction.StartTurnEvent` / `EndTurnEvent`
- `TacticalLevelController.NewTurnEvent`
- `TacticalLevelController.FactionEndedTurnEvent`

## Stable Actor Identification

Primary identifier: **`GeoTacUnitId`** (struct wrapping `int _id`)

```csharp
// PhoenixPoint.Common.Entities\GeoTacUnitId.cs
public struct GeoTacUnitId
{
    private int _id;
    // Serializable — used in GeoCharacter, TacticalActorBase, TacActorUnitResult
}
```

Stored in:
- `TacticalActorBase.GeoUnitId` — set from `TacActorBaseInstanceData.GeoUnitId`
- `TacActorBaseInstanceData.GeoUnitId` — populated during deployment
- `TacActorUnitResult.GeoUnitId` — mission results
- `GeoCharacter._id` — geoscape soldier identifier

Secondary identifier: `SceneObjectId` (struct wrapping `Guid` string) from `ISceneObjectIdTarget`.

Mission identifier: `TacMission.MissionData.MissionId` (Guid).

## Candidate Harmony Patch Locations

| Priority | Target | File:Line | Type | Purpose |
|----------|--------|-----------|------|---------|
| **P0** | `TacticalViewState.ActivateAbility()` | TacticalViewState.cs:289 | Prefix | Universal action validation — serialize `(actorId, abilityDefId, targetData)` and send to host before executing |
| **P1** | `TacticalFaction.RequestEndTurn()` | TacticalFaction.cs:383 | Prefix | Client requests end turn; host validates and broadcasts |
| **P2** | `TacticalLevelController.FireWeaponAtTargetCrt()` | TLC.cs:1539 | Prefix | Host-only: intercept damage application for validation |
| **P3** | `UIStateInventory.AttemptMoveItems()` | UIStateInventory.cs:758 | Prefix | Inventory action validation |
| **P4** | `MoveAbility.Move()` | MoveAbility.cs:123 | Prefix | Movement validation |
