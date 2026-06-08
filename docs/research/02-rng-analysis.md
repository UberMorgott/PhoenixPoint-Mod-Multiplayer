# RNG Implementation & Combat Calculations — Full Analysis

Source: `decompiled\AssemblyCSharp\Assembly-CSharp\src`

## Critical Finding: Decentralized RNG Architecture

Phoenix Point uses **two parallel RNG systems** — neither is suitable for deterministic lockstep without significant modification.

| RNG Source | Seeded? | Usage Scope |
|---|---|---|
| `SharedData.Random` (System.Random) | Time-seeded (`new Random()`) | Deployment selection, map generation, some geoscape rolls |
| `UnityEngine.Random` (engine-managed) | **Unseeded** | **ALL tactical combat**: spread, damage, fumble, AI decisions |

## RNG Source Details

### 1. `SharedData.Random` (System.Random)

```csharp
// PhoenixPoint.Common.Core\SharedData.cs:70
public Random Random = new Random();  // default constructor = time-seeded
```

Usage locations (found 7 direct calls):
- `TacMission.cs:241` — `SharedData.Random.Next() % list2.Count` (deployment set)
- `MapPlot.cs:132` — map generation seed
- `DieAbility.cs:193` — death animation random tag
- `FixedDeployCondition.cs:30` — spawn chance roll
- `InterceptionProjectileSpawner.cs:105` — air combat accuracy
- `InterceptionGameController.cs:230` — interception hit
- `InterceptionAircraft.cs:918` — damage-over-time trigger

### 2. `UnityEngine.Random` (174+ locations)

Used in **all** tactical combat calculations:

**Weapon Spread (Hit Determination):**
```csharp
// PhoenixPoint.Tactical.Entities.Weapons\Weapon.cs:534-561
private Vector3 SpreadInCone(TacticalActor source, Vector3 origin, Vector3 aim, ...)
{
    // Random.Range(0f, 360f) — random angle
    // Random.Range(0f, 1f) — random distance within cone
    // sourceActor.SharedData.GetSpreadFromUniform(Random.Range(0f, 1f), spread) — non-linear distribution
}
```

**Damage Range:**
```csharp
// PhoenixPoint.Tactical.Entities.Effects\DamageEffectDef.cs
public float GenerateDamageAmount() => Random.Range(MinimumDamage, MaximumDamage);
public float GenerateArmorShredAmount() {
    if (Random.Range(0, 100) >= ArmourShredProbabilityPerc) return 0f;
    return ArmourShred;
}
```

**Fumble Check:**
```csharp
// PhoenixPoint.Tactical.Entities.Abilities\TacticalAbility.cs:1218-1225
protected virtual bool FumbleActionCheck() {
    if (base.Source is Equipment && !TacticalActor.IsProficientWithEquipment((Equipment)base.Source)) {
        return Random.Range(0, 100) < ((Equipment)base.Source).EquipmentDef.FumblePerc;
    }
    return false;
}
```

**Weapon Malfunction:**
```csharp
// PhoenixPoint.Tactical.Entities.Weapons\Weapon.cs:1435
if ((float)Random.Range(1, 101) <= (float)CurrentMalfunctionPercent)
```

**Armor Shred:**
```csharp
// PhoenixPoint.Tactical.Entities\DamagePayload.cs:406-417
public float GenerateArmourShredAmount(bool guaranteedShred = false) {
    if (!guaranteedShred && Random.Range(0, 100) >= ArmourShredProbabilityPerc) return 0f;
    ...
}
```

### 3. AI Randomness

The AI system uses `UnityEngine.Random` exclusively:
- `AIFaction.cs:397` — `WeightedRandomElement()` calls `Random.value` (no seedable RNG parameter passed)
- `AIActionYuggothAbility.cs:81` — `Random.Range(0f, 100f)`
- `InterceptionAI.cs:54-56` — `Random.Range()` for AI decisions

## Combat Outcome Determinism

**Combat outcomes are NOT deterministic** under current architecture:
1. Weapon spread uses `UnityEngine.Random` — cannot be seeded externally
2. Damage range uses `UnityEngine.Random`
3. AI decisions use `UnityEngine.Random`
4. The global `SharedData.Random` state alone does not control combat

## Making Combat Deterministic for Host-Only Execution

Required changes:
1. **Replace all `UnityEngine.Random`** calls in tactical combat with a controllable RNG
2. **Introduce a seeded combat RNG** (e.g., mission-seed-based) passed through the action pipeline
3. **Ensure AI's `WeightedRandomElement`** receives a `System.Random` parameter
4. **Either seed `SharedData.Random`** from a known source or replace with controlled instance

## Decision: Host-Only Randomness

- **RNG is the most likely desync source** and must be investigated thoroughly — this analysis is the verdict.
- **All RNG executes on the host; results are broadcast to clients** (consistent with the authoritative-host model, [specs/01-design](../specs/01-design.md) §1). Host-only randomness is feasible precisely *because* we do state-sync rather than lockstep.

### Hidden / Implicit RNG — Prime Desync Risks

Beyond the explicit combat rolls above, flag every **hidden** RNG source — these are the riskiest because they are easy to miss:

- **AI decisions** — `AIFaction.WeightedRandomElement` uses `Random.value` (no seedable RNG). Runs host-only; the resolved enemy turn is streamed to clients.
- **Procedural generation** — map/mission generation (`MapPlot.cs`, `TacMission.cs` deployment) via `SharedData.Random`.
- **Loot / rewards** — any drop or reward roll.
- **Perception / detection rolls** — stealth/detect checks.

All of the above are kept on the host; clients never recompute them.

## Recommended Strategy for Multiplayer

**State-sync (not lockstep) avoids the determinism problem entirely:**

```
Architecture:
  Host runs full game with original RNG (both systems)
  Host broadcasts action RESULTS, not RNG seeds
  Clients reproduce the visual outcome only
  No need for deterministic RNG on clients
```

This is the key architectural decision: **do NOT fight the RNG — embrace state synchronization.**
