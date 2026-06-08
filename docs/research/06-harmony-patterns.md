# Harmony Patch Patterns — Reference from Existing Mods

Source: `refs/`, `AI/AutoAI/`, `PerkOracle/`

## Canonical Mod Lifecycle

```csharp
public class MyMod : ModMain
{
    public override void OnModEnabled()
    {
        var harmony = (Harmony)HarmonyInstance;
        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }

    public override void OnModDisabled()
    {
        // Harmony auto-unpatches on mod disable
    }
}
```

- `CanSafelyDisable = true` — supports hot reload
- For complex mods, set `false` and require restart (TFTV pattern)

## Pattern Reference: Patch Types

| Goal | Patch Type | Pattern |
|------|-----------|---------|
| Validate before executing | **Prefix, return `false`** to block | Check permission → `__result = false; return false;` |
| Intercept but still execute | **Prefix, return `void`** | Modify parameters, original still runs |
| React after execution | **Postfix** | Observe `__result` or `__instance` state |
| Override return value | **Postfix, ref `__result`** | Modify `__result` after original runs |
| Full method replacement | **Prefix, return `false`** + set `__result` | Replace entire logic |
| Patch internal types | `TargetMethod()` + `Prepare()` | Resolve type via reflection at runtime |

## Internal Type Patching (Essential for Multiplayer)

```csharp
[HarmonyPatch]  // empty — type declared via TargetMethod()
public static class MyInternalPatch
{
    public static bool Prepare()
    {
        return ResolveTarget() != null;  // skip if not found
    }

    public static MethodBase TargetMethod()
    {
        Type t = typeof(SomePublicType).Assembly
            .GetType("Namespace.InternalTypeName");
        return t?.GetMethod("MethodName", 
            BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static void Postfix(object __instance)  // use 'object' for internal types
    {
        // ...
    }
}
```

## Private Field Access Hierarchy

1. **`____fieldName` Harmony parameter** — cleanest, but only works for instance fields on the exact `__instance` type
2. **`AccessTools.Field(typeof(T), "name")`** — cache in `static readonly FieldInfo`
3. **`typeof(T).GetField("name", BindingFlags.NonPublic | BindingFlags.Instance)`** — Officer style
4. **Runtime reflection** for types not in ModSDK: `typeof(PublicType).Assembly.GetType("Internal.Type")`

## Def Modification (Non-Harmony)

```csharp
internal static readonly DefRepository Repo = GameUtl.GameComponent<DefRepository>();
var track = (AbilityTrackDef)Repo.GetDef("guid-here");
track.AbilitiesByLevel[5].Ability = CustomAbilityDef;
```

## Exception Handling

Wrap every patch body in try/catch:
```csharp
// Prefix: rethrow on exception (to not break the game)
try { /* logic */ }
catch (Exception e) { LogError(e); throw; }

// Postfix: swallow errors (original already ran)
try { /* logic */ }
catch (Exception e) { LogError(e); }
```

## Event Subscription Pattern

```csharp
// Subscribe in OnLevelStart(), unsubscribe in OnLevelEnd()
private TacticalLevelController.NewTurnEventHandler _onNewTurn;

public void OnLevelStart()
{
    _onNewTurn = (prev, next) => { /* network update */ };
    GameUtl.GetGameComponent<TacticalLevelController>().NewTurnEvent += _onNewTurn;
}

public void OnLevelEnd()
{
    if (_onNewTurn != null)
    {
        var controller = GameUtl.GetGameComponent<TacticalLevelController>();
        if (controller != null)
            controller.NewTurnEvent -= _onNewTurn;
        _onNewTurn = null;
    }
}
```
