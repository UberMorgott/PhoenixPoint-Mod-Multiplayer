# Replication Increment-1 — Client Inert + Slaved-Clock Travel Mirror Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. TDD, DRY, YAGNI, frequent commits — commit straight to inner `main` (NO branch, NO push); full test suite green before each commit.

**Goal:** Make the **client geoscape engine inert** — it runs ZERO stochastic/clock-driven authoritative simulation and emits ZERO travel authority — while the existing `0x34` clock mirror keeps its clock advancing so the engine's own display coroutines render the host's living world (vehicles fly in sync) with no local double-sim divergence.

**Architecture:** Host-authoritative thin-client (fixed; PP is non-deterministic so lockstep is unviable). Two new client-only Harmony patch classes plus one pure helper table: (A) `ClientGeoSimSuppressPatch` prefixes the CLOSED 13-producer set of `NextUpdate`-returning `Timing` callbacks, returning `false` + `__result = NextUpdate.Never` so each producer permanently stops on the client; (B) `ClientTravelEmitterSuppressPatch` prefixes the three `GeoVehicle` travel emitters (`set_Travelling` side-effects, `InitiateTravelling`, `OnArrived`), returning `false` so the client renders motion via the whitelisted `NavigateRoutine` but never mutates its own authoritative geoscape state. (C) is a verify-only checkpoint that the already-live `TimeBridge.ApplyTimeState` (`0x34`/`0x01`) advances the slaved client clock so `NavigateRoutine` renders. Both patch classes gate strictly client-only (host simulates normally) and are best-effort by design (backstopped by host state-diff + CRC reload in later increments).

**Tech Stack:** C# (net472), HarmonyLib (`AccessTools` dynamic resolution + `TargetMethods()` multi-target single-prefix; mod never hard-references game types), xUnit 2.9.2 (`Multiplayer.Tests`), existing `NetworkEngine` client gate (`Instance`/`IsActive`/`IsHost`), existing `CommandRelay.IsApplying` re-entrancy guard, existing `TimeBridge`/`0x34` clock mirror (time-sync Increment-1, already shipped).

---

## Grounding notes (decompile-verified — read before coding)

> Decompile root: `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src` (gitignored — verified via Bash grep + Read on real files). Mod source: `E:\DEV\PhoenixPoint\Multiplayer\src`.

### Mod conventions (copy these exactly)
- **Client gate predicate** (every existing intercept patch, e.g. `src/Harmony/StartTravelInterceptPatch.cs:36-39`, `src/Harmony/TimePauseInterceptPatch.cs:29-33`):
  ```csharp
  var engine = NetworkEngine.Instance;
  if (engine == null || !engine.IsActive) return true;  // single player: pass through
  if (CommandRelay.IsApplying) return true;             // re-entrant host-approved apply: execute
  if (engine.IsHost) return true;                       // host simulates normally
  // ...client-only body...
  ```
  `NetworkEngine.IsActive` / `NetworkEngine.IsHost` are `public bool { get; private set; }` auto-properties (`src/Network/NetworkEngine.cs:14-15`).
- **Patch class shape** (`src/Harmony/StartTravelInterceptPatch.cs`, `CurtainShowPatch.cs`): `[HarmonyPatch]` attribute on a `public static class`; a `static bool Prepare()` that resolves engine types via `AccessTools.TypeByName("…")` and returns `false` if absent (Harmony skips the class — never crashes); `static MethodBase TargetMethod()` (single) **or** `static IEnumerable<MethodBase> TargetMethods()` (multi); a `static bool Prefix(...)`. Game types are NEVER hard-referenced — injected params typed `object`; non-void `__result` typed `ref object`.
- **Registration is automatic:** `MultiplayerMain.OnModEnabled:26-27` does `harmony.PatchAll(Assembly.GetExecutingAssembly())`. New `[HarmonyPatch]` classes auto-discover; **no manual registration**.
- **Main csproj** (`Multiplayer.csproj`) has no `EnableDefaultCompileItems=false` → SDK globs `src/**/*.cs`. **New `src/` files need no csproj edit.**
- **Test csproj** (`Multiplayer.Tests/Multiplayer.Tests.csproj`) HAS `EnableDefaultCompileItems=false` (`:7`); pure src cores are linked individually via `<Compile Include="..\src\…\X.cs"><Link>X.cs</Link></Compile>` (`:14-29`). **A new pure helper that needs a unit test MUST get its own `<Compile>` link line.** Existing links include `CommandCodec.cs`, `PermissionGate.cs`, `InterceptRegistry.cs`, `MessageSerializer.cs`.
- **Auditable declarative-table pattern:** `src/Network/CommandSync/InterceptRegistry.cs` (one row per type, `static readonly` collection, public read accessors). The producer table mirrors this style so the suppress set is auditable/extendable.

### The 13 producers — VERIFIED signatures (all identical shape)

Every producer is a `private NextUpdate <Name>(Timing <param>)` instance callback started via `Timing.Start(<Name>, …)`. **NONE is an `IEnumerator` coroutine.** Therefore the correct no-op is a **prefix returning `false` + `__result = NextUpdate.Never`**: `TimingScheduler` (`Base.Core/TimingScheduler.cs:516-518`) calls `updateable.Stop(...)` when the returned `NextUpdate == Never`, permanently de-registering the producer on the client (zero further fires). `NextUpdate.Never` is `public static readonly` (`Base.Core/NextUpdate.cs:15`).

| # | Class | Method (decl line) | Sig | `Timing.Start` schedule sites | Suppress no-op |
|---|---|---|---|---|---|
| 1 | `PhoenixPoint.Geoscape.Levels.GeoLevelController` | `LevelHourlyUpdateCrt` (`:777`) | `private NextUpdate (Timing)` | `:761` | `__result=Never; return false` |
| 2 | `PhoenixPoint.Geoscape.Entities.VehicleFactionController` | `RescheduleDestinationCrt` (`:185`) | `private NextUpdate (Timing)` | `:123`/`:180`/`:235` | `__result=Never; return false` |
| 3 | `PhoenixPoint.Geoscape.Entities.Sites.GeoScavengingSite` | `RefreshEnemyAtSiteCrt` (`:99`) | `private NextUpdate (Timing)` | `:96`/`:135` | `__result=Never; return false` |
| 4 | `PhoenixPoint.Geoscape.Entities.GeoAlienBase` | `ExpandAlienBase` (`:291`) | `private NextUpdate (Timing)` | `:154`/`:479` | `__result=Never; return false` |
| 5 | `PhoenixPoint.Geoscape.Entities.GeoScanner` | `Expand` (`:72`) | `private NextUpdate (Timing)` | `:135` | `__result=Never; return false` |
| 6 | `PhoenixPoint.Geoscape.Entities.SiteSurroundingsScanner` | `ExpandSiteScanner` (`:273`) | `private NextUpdate (Timing)` | `:213` | `__result=Never; return false` |
| 7 | `PhoenixPoint.Geoscape.Entities.GeoAncientSiteProbe` | `CompleteScanCrt` (`:47`) | `private NextUpdate (Timing)` | `:77` | `__result=Never; return false` |
| 8 | `PhoenixPoint.Geoscape.Entities.MistRepeller` | `ExpansMistRepeller` (`:104`) | `private NextUpdate (Timing)` | `:99` | `__result=Never; return false` |
| 9 | `PhoenixPoint.Geoscape.Entities.GeoBehemothActor` | `SubmergeCrt` (`:578`) | `private NextUpdate (Timing)` | `:300`/`:575` | `__result=Never; return false` |
| 10 | `PhoenixPoint.Geoscape.Entities.GeoBehemothActor` | `EmergeCrt` (`:659`) | `private NextUpdate (Timing)` | `:706` | `__result=Never; return false` |
| 11 | `PhoenixPoint.Geoscape.Entities.GeoVehicle` | `SiteExplorationCompleted` (`:463`) | `private NextUpdate (Timing)` | `:440` (via `ExploreCurrentSite:437`/replay `:1138`) | `__result=Never; return false` |
| 12 | `PhoenixPoint.Geoscape.Entities.Sites.GeoHarvestingSite` | `ResourceHarvestedCompleted` (`:118`) | `private NextUpdate (Timing)` | `:112`/`:166` | `__result=Never; return false` |
| 13 | `PhoenixPoint.Geoscape.MistRendererSystem` | `UpdateMist` (`:384`) | `private NextUpdate (Timing)` | `:201` | `__result=Never; return false` |

> **Overload trap (producer #4):** `GeoAlienBase` has a SECOND `ExpandAlienBase(IConsole)` static console command (`:580`). Disambiguate by parameter type `Base.Core.Timing` — resolve with `AccessTools.Method(type, "ExpandAlienBase", new[] { timingType })`.
>
> **`Timing` param type:** resolve once via `AccessTools.TypeByName("Base.Core.Timing")` for overload disambiguation on every producer (all take a single `Timing` parameter).

### Whitelist — DO NOT patch (keep alive on client)
- `GeoNavComponent.NavigateRoutine` (`GeoNavComponent.cs:94`, `IEnumerator<NextUpdate>`) — pure render: `Slerp` (`:117`), pivot rotation (`:119`), `RangeRemaining` (`:124`); clock-pure, RNG-free. This is what renders the synced ship.
- `MistRendererSystem.FrameUpdate` (`:405`, started `:205` on `GameTiming`) — cosmetic frame mist.
- `GeoscapeLog.ProcessQueuedEvents` (`:67`).

### The 3 travel emitters — VERIFIED (all on `GeoVehicle`)
`GeoNavComponent.NavigateRoutine` (the whitelisted renderer) weaves three authoritative `GeoVehicle` calls inline with the render math:
- `:96`/`:102` `NavActor.Travelling = true`; `:146` `NavActor.Travelling = false` → `set_Travelling(bool)` side-effect `GeoVehicle.cs:212-216`: `CurrentSite.VehicleLeft(this)` + `CurrentSite = null`.
- `:108` `NavActor.InitiateTravelling()` → `GeoVehicle.cs:587-591`: `Animator.SetInteger("State",1)` [cosmetic] + `TravelStartedEvent?.Invoke(this)` [authoritative].
- `:147` `Arrived?.Invoke(...)` → wired (`:309`) to `GeoVehicle.OnArrived(Vector3,bool)` `:315-338`: `CurrentSite = geoSite`, `_destinationSites.RemoveAt(0)`, `Animator.SetInteger("State",0)` [cosmetic], `RefreshVisibility`, `CurrentSite.VehicleArrived(this)`, `OnArrivedAtDestination → ArrivedAtDestinationEvent` [authoritative].

Because the render and the emitters share ONE coroutine, **`NavigateRoutine` itself must not be patched** (that would kill render). Suppress the emitters at their `GeoVehicle` seam: prefix `set_Travelling` / `InitiateTravelling` / `OnArrived(Vector3,bool)`.

### Travel-emitter gate is STRICTER than the CommandSync gate (design decision T1)
The CommandSync intercept patches keep an `IsApplying → return true` carve-out so a host-approved action re-applies on the client. The travel **emitters** are NOT commands — they are sim side-effects the client must NEVER produce (the host owns `CurrentSite`/site occupancy and pushes them via `0x36`/`0x35` in INC 2/3). So the travel-emitter prefixes suppress on the client **unconditionally** (NO `IsApplying` carve-out): gate = `engine != null && engine.IsActive && !engine.IsHost → return false`. Consequence in INC 1 (no diff yet): the client ship flies in sync (acceptance #1) but its `CurrentSite`/`_traveling` stay stale until INC 2/3 — that is the documented INC-1 boundary. The cosmetic `Animator` state (flight/landed sprite) is also suppressed; accepted (self-corrects, cosmetic only).

### Scope-C seam (already shipped — verify only)
- `TimeBridge.ApplyTimeState(TimeStatePayload)` (`src/Network/CommandSync/TimeBridge.cs:107-122`) builds a `Base.Core.TimingInstanceData` and calls `Timing.ProcessInstanceData` — writes `_paused/_scale/StartTime/OwnNow/…` directly, fires **no** events, reschedules nothing (`Timing.cs:222-232`). Driven by the live `0x34`/`0x01` client mirror (`ClientTimeMirror.Apply`). This advances the client `GeoLevelController.Timing.Now` between packets at host scale → `NavigateRoutine`'s `Ratio01(startTime, Now)` progresses → ship renders motion. **No new code if it advances; if it pauses, the one-line fix is to ensure the client clock is not force-paused (it is not — `ProcessInstanceData` writes `_paused` from the host snapshot, so a running host keeps the client running).**

---

## File Structure

### New files

| File | Responsibility | Pure / Unity-free? |
|------|----------------|--------------------|
| `src/Network/CommandSync/GeoSimProducerTable.cs` | Pure, auditable declarative table of the 13 producer `(DeclaringTypeName, MethodName)` rows + read accessor. No game types — plain strings. Single source of truth for the suppress set. | **Pure → unit-tested** |
| `src/Harmony/ClientGeoSimSuppressPatch.cs` | Client-only multi-target Harmony patch: `TargetMethods()` resolves each `GeoSimProducerTable` row (disambiguated by the `Timing` param) → one `Prefix` sets `__result = NextUpdate.Never` + `return false` on the client. | Engine/Harmony — build + 2-instance |
| `src/Harmony/ClientTravelEmitterSuppressPatch.cs` | Client-only multi-target Harmony patch: `TargetMethods()` yields `GeoVehicle.set_Travelling(bool)`, `InitiateTravelling()`, `OnArrived(Vector3,bool)` → one `Prefix` returns `false` on the client (unconditional, no `IsApplying` carve-out). | Engine/Harmony — build + 2-instance |
| `Multiplayer.Tests/GeoSimProducerTableTests.cs` | xUnit: the table has exactly 13 rows, no nulls/blanks, no duplicate `(type,method)`, includes the canonical anchors, excludes the whitelist. | Test |

### Modified files

| File | Change |
|------|--------|
| `Multiplayer.Tests/Multiplayer.Tests.csproj` | Add `<Compile Include="..\src\Network\CommandSync\GeoSimProducerTable.cs"><Link>GeoSimProducerTable.cs</Link></Compile>` so the pure table links into the test assembly. |

**Build:** `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
**Tests:** `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
**In-game (2-instance):** per `multiplayer-second-instance-setup` — host one geoscape campaign, join with a second Goldberg-emu instance; see Task 5 checkpoint.

---

## Task 1 — Pure producer table (`GeoSimProducerTable`), full TDD

**Files:**
- Create: `src/Network/CommandSync/GeoSimProducerTable.cs`
- Modify: `Multiplayer.Tests/Multiplayer.Tests.csproj` (add the `<Compile>` link)
- Test: `Multiplayer.Tests/GeoSimProducerTableTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — Create `Multiplayer.Tests/GeoSimProducerTableTests.cs`:

```csharp
using System.Linq;
using Multiplayer.Network.CommandSync;
using Xunit;

public class GeoSimProducerTableTests
{
    [Fact]
    public void Producers_HasExactlyThirteenRows()
    {
        Assert.Equal(13, GeoSimProducerTable.Producers.Count);
    }

    [Fact]
    public void Producers_NoBlankFields()
    {
        foreach (var p in GeoSimProducerTable.Producers)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.DeclaringTypeName));
            Assert.False(string.IsNullOrWhiteSpace(p.MethodName));
        }
    }

    [Fact]
    public void Producers_NoDuplicateTypeMethodPairs()
    {
        var keys = GeoSimProducerTable.Producers
            .Select(p => p.DeclaringTypeName + "::" + p.MethodName)
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("PhoenixPoint.Geoscape.Levels.GeoLevelController", "LevelHourlyUpdateCrt")]
    [InlineData("PhoenixPoint.Geoscape.Entities.GeoAlienBase", "ExpandAlienBase")]
    [InlineData("PhoenixPoint.Geoscape.Entities.GeoBehemothActor", "SubmergeCrt")]
    [InlineData("PhoenixPoint.Geoscape.Entities.GeoBehemothActor", "EmergeCrt")]
    [InlineData("PhoenixPoint.Geoscape.MistRendererSystem", "UpdateMist")]
    public void Producers_ContainsAnchor(string type, string method)
    {
        Assert.Contains(GeoSimProducerTable.Producers,
            p => p.DeclaringTypeName == type && p.MethodName == method);
    }

    [Theory]
    [InlineData("PhoenixPoint.Geoscape.Entities.GeoNavComponent", "NavigateRoutine")]
    [InlineData("PhoenixPoint.Geoscape.MistRendererSystem", "FrameUpdate")]
    [InlineData("PhoenixPoint.Geoscape.GeoscapeLog", "ProcessQueuedEvents")]
    public void Producers_ExcludesWhitelist(string type, string method)
    {
        Assert.DoesNotContain(GeoSimProducerTable.Producers,
            p => p.DeclaringTypeName == type && p.MethodName == method);
    }
}
```

- [ ] **Step 2: Run test to verify it fails** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release --filter GeoSimProducerTableTests`
  Expected: FAIL — compile error (`GeoSimProducerTable` / `Producers` do not exist).

- [ ] **Step 3: Create the pure table** — Create `src/Network/CommandSync/GeoSimProducerTable.cs`:

```csharp
using System.Collections.Generic;

namespace Multiplayer.Network.CommandSync
{
    // Auditable, pure (Unity-free) declarative list of the CLOSED 13-producer geoscape-sim set the
    // CLIENT must suppress so it runs ZERO stochastic/clock-driven authoritative simulation (SD-AIDR
    // INC-1). Every entry is a `private NextUpdate <Method>(Timing)` callback started via Timing.Start;
    // ClientGeoSimSuppressPatch resolves each row (disambiguated by the single Base.Core.Timing param)
    // and prefixes it to return NextUpdate.Never on the client. Best-effort by design — a missed/renamed
    // producer is bounded local jitter, self-healed by the host state-diff (INC 3) + CRC reload (INC 5).
    //
    // WHITELIST (must NOT appear here — render/cosmetic/log, kept alive on client):
    //   GeoNavComponent.NavigateRoutine, MistRendererSystem.FrameUpdate, GeoscapeLog.ProcessQueuedEvents.
    //
    // Decompile-verified decl lines (E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\...\src):
    //   GeoLevelController.LevelHourlyUpdateCrt:777 (sched :761)
    //   VehicleFactionController.RescheduleDestinationCrt:185 (sched :123/:180/:235)
    //   GeoScavengingSite.RefreshEnemyAtSiteCrt:99 (sched :96/:135)
    //   GeoAlienBase.ExpandAlienBase:291 (sched :154/:479) [overload trap: static ExpandAlienBase(IConsole):580 -> pin Timing param]
    //   GeoScanner.Expand:72 (sched :135)
    //   SiteSurroundingsScanner.ExpandSiteScanner:273 (sched :213)
    //   GeoAncientSiteProbe.CompleteScanCrt:47 (sched :77)
    //   MistRepeller.ExpansMistRepeller:104 (sched :99)
    //   GeoBehemothActor.SubmergeCrt:578 (sched :300/:575)
    //   GeoBehemothActor.EmergeCrt:659 (sched :706)
    //   GeoVehicle.SiteExplorationCompleted:463 (sched :440 via ExploreCurrentSite:437 / replay :1138)
    //   GeoHarvestingSite.ResourceHarvestedCompleted:118 (sched :112/:166)
    //   MistRendererSystem.UpdateMist:384 (sched :201)
    public sealed class GeoSimProducer
    {
        public string DeclaringTypeName; // AccessTools.TypeByName key (full namespaced name)
        public string MethodName;        // the NextUpdate(Timing) callback on that type
    }

    public static class GeoSimProducerTable
    {
        public static readonly IReadOnlyList<GeoSimProducer> Producers = new[]
        {
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Levels.GeoLevelController",          MethodName = "LevelHourlyUpdateCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.VehicleFactionController",  MethodName = "RescheduleDestinationCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.Sites.GeoScavengingSite",  MethodName = "RefreshEnemyAtSiteCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoAlienBase",             MethodName = "ExpandAlienBase" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoScanner",               MethodName = "Expand" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.SiteSurroundingsScanner",  MethodName = "ExpandSiteScanner" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoAncientSiteProbe",      MethodName = "CompleteScanCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.MistRepeller",             MethodName = "ExpansMistRepeller" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoBehemothActor",         MethodName = "SubmergeCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoBehemothActor",         MethodName = "EmergeCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoVehicle",               MethodName = "SiteExplorationCompleted" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.Sites.GeoHarvestingSite",  MethodName = "ResourceHarvestedCompleted" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.MistRendererSystem",                MethodName = "UpdateMist" }
        };
    }
}
```

- [ ] **Step 4: Link the pure table into the test assembly** — In `Multiplayer.Tests/Multiplayer.Tests.csproj`, immediately after the `InterceptRegistry.cs` link line (`:29`), add:

```xml
    <Compile Include="..\src\Network\CommandSync\GeoSimProducerTable.cs"><Link>GeoSimProducerTable.cs</Link></Compile>
```

- [ ] **Step 5: Run test to verify it passes** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release --filter GeoSimProducerTableTests`
  Expected: PASS (all 11 cases: 1 count + 1 blank + 1 dup + 5 anchors + 3 whitelist).

- [ ] **Step 6: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(replication): auditable 13-producer geoscape-sim suppress table (pure, TDD)"
```

---

## Task 2 — `ClientGeoSimSuppressPatch` (client-only 13-producer suppression), build-verified

**Files:**
- Create: `src/Harmony/ClientGeoSimSuppressPatch.cs`

> **No unit test** — Harmony patch over live game types (`AccessTools` resolution + `__result` injection); not in the Unity-free test set, exactly like the existing intercept patches. The producer table it consumes is covered by Task 1. Verification = compiles (`Prepare` links) + Task-5 in-game.

- [ ] **Step 1: Create the patch** — Create `src/Harmony/ClientGeoSimSuppressPatch.cs`:

```csharp
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.CommandSync;
using UnityEngine;

namespace Multiplayer.Harmony
{
    // SD-AIDR INC-1 (A): make the CLIENT geoscape engine inert. Prefixes the CLOSED 13-producer set
    // (GeoSimProducerTable) of `private NextUpdate <Method>(Timing)` callbacks. On a client in an active
    // session the prefix sets __result = NextUpdate.Never and returns false: the scheduler then Stops the
    // updateable (TimingScheduler.cs:516-518), permanently de-registering that producer on the client ->
    // ZERO local stochastic/clock-driven authoritative sim. Host / single-player run normally. Best-effort:
    // a missed producer is bounded jitter, self-healed by host diff (INC 3) + CRC reload (INC 5).
    //
    // One [HarmonyPatch] class, many targets via TargetMethods() (Harmony multi-target single-prefix).
    // Game types are NEVER hard-referenced: targets resolve via AccessTools.TypeByName/Method (disambiguated
    // by the single Base.Core.Timing param), and __result is typed `ref object` (boxed NextUpdate).
    [HarmonyPatch]
    public static class ClientGeoSimSuppressPatch
    {
        // Boxed NextUpdate.Never, resolved once in Prepare(); assigned into __result to stop each producer.
        private static object _never;

        public static bool Prepare()
        {
            var nextUpdateType = AccessTools.TypeByName("Base.Core.NextUpdate");
            if (nextUpdateType == null) return false; // engine not loaded -> Harmony skips this class
            _never = AccessTools.Field(nextUpdateType, "Never")?.GetValue(null);
            return _never != null;
        }

        // Resolve every producer's NextUpdate(Timing) callback. Skip (do not yield) any that fail to resolve
        // so an absent/renamed engine method never crashes PatchAll -- best-effort by design.
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var timingType = AccessTools.TypeByName("Base.Core.Timing");
            if (timingType == null) yield break;
            foreach (var p in GeoSimProducerTable.Producers)
            {
                var t = AccessTools.TypeByName(p.DeclaringTypeName);
                if (t == null) continue;
                // Pin the Timing-param overload (e.g. GeoAlienBase.ExpandAlienBase(Timing) vs ...(IConsole)).
                var m = AccessTools.Method(t, p.MethodName, new[] { timingType });
                if (m != null) yield return m;
            }
        }

        // __result : ref object (the producer returns Base.Core.NextUpdate, which the mod never references).
        // Returning false skips the heavy producer body; __result = NextUpdate.Never makes the scheduler Stop
        // the updateable so it never re-fires on the client. Host / SP / re-entrant apply -> run the body.
        public static bool Prefix(ref object __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // single player: simulate normally
            if (CommandRelay.IsApplying) return true;            // host-approved apply: let it run
            if (engine.IsHost) return true;                      // host is the sole simulator

            __result = _never;                                   // client: stop the producer, no local sim
            return false;
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles** —
  Run: `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run full suite to verify no regression** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
  Expected: all tests PASS (Task-1 cases + every pre-existing test green).

- [ ] **Step 4: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(replication): ClientGeoSimSuppressPatch - client runs zero geoscape sim (13 producers -> NextUpdate.Never)"
```

---

## Task 3 — `ClientTravelEmitterSuppressPatch` (client-only 3 travel emitters), build-verified

**Files:**
- Create: `src/Harmony/ClientTravelEmitterSuppressPatch.cs`

> **No unit test** — Harmony patch over `GeoVehicle`; not in the Unity-free test set. Verification = compiles + Task-5 in-game.

- [ ] **Step 1: Create the patch** — Create `src/Harmony/ClientTravelEmitterSuppressPatch.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Harmony
{
    // SD-AIDR INC-1 (B): on the CLIENT, suppress the THREE authoritative travel emitters that
    // GeoNavComponent.NavigateRoutine (whitelisted renderer) calls inline on its GeoVehicle (NavActor):
    //   1. set_Travelling(bool) -> CurrentSite.VehicleLeft(this) + CurrentSite = null (GeoVehicle.cs:212-216)
    //   2. InitiateTravelling()  -> TravelStartedEvent?.Invoke (GeoVehicle.cs:587-591)  [+ cosmetic anim]
    //   3. OnArrived(Vector3,bool) -> CurrentSite assign + _destinationSites pop + VehicleArrived +
    //                                 ArrivedAtDestinationEvent (GeoVehicle.cs:315-338)  [+ cosmetic anim]
    // NavigateRoutine's pure render (Slerp pos/rot/RangeRemaining) is NOT patched and keeps running on the
    // slaved clock -> the client ship flies in sync; only the host-owned authoritative emitters are dropped.
    //
    // GATE (T1, stricter than the CommandSync intercepts): these are sim SIDE-EFFECTS, not commands, so the
    // client must NEVER produce them -- suppress UNCONDITIONALLY on a client (NO IsApplying carve-out). The
    // host owns CurrentSite/site occupancy and pushes them via 0x36/0x35 in INC 2/3. INC-1 consequence:
    // client CurrentSite/_traveling stay stale until the diff lands (documented INC-1 boundary); the
    // cosmetic Animator state is also suppressed (self-correcting, accepted).
    [HarmonyPatch]
    public static class ClientTravelEmitterSuppressPatch
    {
        public static bool Prepare()
        {
            return AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle") != null;
        }

        // Yield the three GeoVehicle emitter seams; skip any that fail to resolve (best-effort).
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var gv = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (gv == null) yield break;

            // 1. Travelling setter (the side-effects, not the field write per se).
            var setTravelling = AccessTools.PropertySetter(gv, "Travelling");
            if (setTravelling != null) yield return setTravelling;

            // 2. InitiateTravelling() - public, no params.
            var initiate = AccessTools.Method(gv, "InitiateTravelling", Type.EmptyTypes);
            if (initiate != null) yield return initiate;

            // 3. OnArrived(Vector3, bool) - private; pin the param types.
            var onArrived = AccessTools.Method(gv, "OnArrived",
                new[] { typeof(UnityEngine.Vector3), typeof(bool) });
            if (onArrived != null) yield return onArrived;
        }

        // Client in an active session -> return false (suppress the authoritative emitter). Host / SP run it.
        // No IsApplying carve-out: the client must never emit travel authority locally (INC-1 design T1).
        public static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // single player
            if (engine.IsHost) return true;                      // host emits authoritatively
            return false;                                        // client: suppress (render-only)
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles** —
  Run: `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run full suite to verify no regression** —
  Run: `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
  Expected: all tests PASS.

- [ ] **Step 4: Commit** —

```bash
git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "feat(replication): ClientTravelEmitterSuppressPatch - suppress 3 GeoVehicle travel emitters on client (render-only)"
```

---

## Task 4 — Verify scope C: slaved `0x34` clock advances the client display (no new code expected)

**Files:**
- Read-only verify: `src/Network/CommandSync/TimeBridge.cs:107-122` (`ApplyTimeState`), `src/Network/CommandSync/ClientTimeMirror.cs`, `src/Network/NetworkEngine.cs` (`0x34`/`0x01` receive → `ClientTimeMirror.Apply`).

> This is a code-confirmation task, not a code-change task. INC-1 scope C states: confirm the existing `0x34`/`0x01` mirror (`TimeBridge.ApplyTimeState` → `Timing.ProcessInstanceData`) makes the client `GeoLevelController.Timing` ADVANCE (slaved, not paused) so the whitelisted `NavigateRoutine` renders motion. No new code if it advances; the only possible fix is a one-liner ensuring the client clock is not force-paused.

- [ ] **Step 1: Confirm the apply path writes the running clock state** — Read `src/Network/CommandSync/TimeBridge.cs:107-122`. Verify `ApplyTimeState` sets `Paused`, `Scale`, `OwnNow` (etc.) from the host snapshot onto a `Base.Core.TimingInstanceData` and calls `Timing.ProcessInstanceData`. Expected: it does (already shipped). Because `Paused`/`Scale` come from the HOST snapshot, a running host (`Paused=false`, `Scale>0`) keeps the client clock running and advancing between packets — NOT force-paused.

- [ ] **Step 2: Confirm the receive wiring is live** — In `src/Network/NetworkEngine.cs`, confirm the `0x34` (`CampaignStateUpdate`) receive-case decodes subtype `0x01` and calls `ClientTimeMirror.Apply(...)` (time-sync Increment-1, commit `70ce041`). Expected: present.

- [ ] **Step 3: Confirm `NavigateRoutine` reads the same clock** — Read `decompiled/.../GeoNavComponent.cs:106,112`: `startTime = NavActor.Actor.Timing.Now` and `totalTime.Ratio01(startTime, NavActor.Actor.Timing.Now)`. The actor's `Timing` chains to `GeoLevelController.Timing` (the clock the mirror writes). Expected: a re-pinned, advancing `Now` drives `Ratio01` 0→1 → the ship moves. No code change.

- [ ] **Step 4: (Conditional) one-line fix only if the client clock is paused** — IF, during the Task-5 in-game checkpoint, the client clock is found force-paused (ship frozen despite a running host), the fix is to ensure no client path holds `Timing.Paused = true` independently; the `0x34` mirror is authoritative. Record the exact offending line in the in-game report before changing anything (do NOT pre-emptively edit — scope C is verify-first).

- [ ] **Step 5: No commit** unless Step 4 produced a real one-line fix. If it did, commit:

```bash
git -C E:\DEV\PhoenixPoint\Multiplayer add -A && git -C E:\DEV\PhoenixPoint\Multiplayer commit -m "fix(replication): keep client geoscape clock slaved-advancing (0x34 mirror authoritative)"
```

---

## Task 5 — In-game 2-instance checkpoint (USER runs this)

**Files:** none (integration verification only).

> Harmony/Unity patches are not unit-testable. Final verification is a 2-instance in-game run per the `multiplayer-second-instance-setup` memory (Goldberg-emu second copy + `mklink /J` junctions). The USER performs this; the agent records the outcome.

- [ ] **Step 1: Launch two instances** — Start the host instance + the Goldberg-emu second instance. Host creates/loads a geoscape campaign; the second instance joins (lobby → ready → host picks save → transfer → barrier → play). Confirm both reach the geoscape.

- [ ] **Step 2: Verify the client ship flies in sync (acceptance #1)** — On the HOST, order a Phoenix aircraft to travel to a distant site. EXPECT on the CLIENT: the same aircraft flies along the same path and arrives at (visually) the same time, driven purely by the slaved clock + whitelisted `NavigateRoutine` render (no position bytes). This is the original `StartTravel` desync being gone.

- [ ] **Step 2b: Verify the client clock advances (scope C)** — Watch the client geoscape clock label: it must tick forward in lockstep with the host's (re-pinned ~2-5Hz, ticking at host scale between packets). A frozen client clock means scope-C Task-4 Step-4 applies — report the symptom.

- [ ] **Step 3: Verify the client no longer self-simulates / diverges** — Let several in-game hours pass with the host driving. EXPECT on the CLIENT: no independent hour-tick income/research jumps, no client-only faction-ship destination changes, no client-only site-enemy refresh, no client-only alien-base expansion, no client-only behemoth surface/submerge, no client-only mist changes, no "vehicle N not found" self-sim artifacts. The client world should mirror the host, not run its own divergent sim. (Authoritative state like the client's own `CurrentSite` after arrival may lag — that is the documented INC-1 boundary, fixed in INC 2/3.)

- [ ] **Step 4: Verify the host is unaffected** — On the HOST, the same campaign simulates fully normally (income, faction traffic, expansions all proceed). The suppression must be client-only.

- [ ] **Step 5: Record the outcome** — Append the result (PASS / FAIL per step, plus any frozen-clock or residual-divergence note) to `docs/research/00-current-state.md` via SCRIBE, and update the in-game-test status. If Step 2b shows a frozen clock, run Task-4 Step-4 then re-test.

---

## Self-Review

**1. Spec coverage (INC-1 scope A/B/C):**
- **A. `ClientGeoSimSuppressPatch` over the closed 13-producer set, client-only, correct no-op per signature, auditable table** → Task 1 (pure auditable table, TDD) + Task 2 (the patch; all 13 verified as `NextUpdate(Timing)` → `__result = NextUpdate.Never` + `return false`; overload trap #4 pinned by `Timing` param; whitelist excluded and asserted in Task 1). ✓
- **B. Travel-emitter suppression of the THREE `GeoVehicle` emitters, client-only, `NavigateRoutine` render kept alive** → Task 3 (`set_Travelling` / `InitiateTravelling` / `OnArrived(Vector3,bool)`; mechanism = patch the `GeoVehicle` seams, NOT `NavigateRoutine`; gate decision T1 documented). ✓
- **C. Verify the existing `0x34`/`0x01` mirror advances the slaved client clock; one-line fix only if paused** → Task 4 (verify-first, conditional fix) + Task 5 Step-2b (in-game). ✓
- **Gate all prefixes client-only (host normal)** → both patches use `NetworkEngine.Instance/IsActive/IsHost`; geo-sim keeps the `IsApplying` carve-out, travel-emitters intentionally do not (T1). ✓
- **Testing reality** → pure helper TDD-first (Task 1), patches build-verified + suite-green (Tasks 2-3), explicit in-game checkpoint (Task 5). ✓

**2. Placeholder scan:** No "TBD/TODO/handle edge cases/similar to Task N". Every code step shows complete code; every command shows expected output. The only conditional ("one-line fix") is scope-C's spec-mandated verify-first branch with an explicit pre-condition and recorded trigger — not a placeholder. ✓

**3. Type/name consistency:**
- `GeoSimProducerTable.Producers` (`IReadOnlyList<GeoSimProducer>`) defined in Task 1, consumed identically in Task 2's `TargetMethods()`. Field names `DeclaringTypeName`/`MethodName` consistent across table, tests, and patch. ✓
- `NextUpdate.Never` fetched as boxed `object _never` in Task 2 `Prepare()` and assigned to `ref object __result` in `Prefix` — type-consistent (mod never references `NextUpdate`). ✓
- Client gate (`NetworkEngine.Instance`/`IsActive`/`IsHost`, `CommandRelay.IsApplying`) matches the live accessors verified in existing patches. ✓
- `AccessTools.PropertySetter(gv,"Travelling")` / `AccessTools.Method(gv,"InitiateTravelling",Type.EmptyTypes)` / `AccessTools.Method(gv,"OnArrived",{Vector3,bool})` match the decompiled `GeoVehicle` members. ✓

No gaps found.

---

## Out of scope (later increments — do NOT build here)
- **INC 2** — `0x36 GeoEntityOp` (host-created vehicles/sites appear/despawn on client via native `Initialize`/`DoEnterPlay`).
- **INC 3** — `0x35 GeoStateDiff` (generic InstanceData-diff + marketplace blob + haven-defense progress).
- **INC 4** — input generalization (`GeoAbility.Activate` → CommandSync, 18 intents; launch-loop gating; retire the per-action `StartTravel` patch).
- **INC 5** — rolling CRC32 divergence detection + two-barrier reload backstop.
