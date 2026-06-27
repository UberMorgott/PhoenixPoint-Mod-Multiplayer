# Inc3 — Combat outcome + explosion VFX + enemy-turn camera — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the co-op client follow and see the host's enemy turn — camera chases each enemy move/shot/melee, grenade explosions and damage ordering verified — closing the remaining Inc3 gaps.

**Architecture:** Approach B (chase injection). The client's enemy replay coroutines bypass `TacticalAbility.Activate`, so no native camera hint fires. We add a static enemy-turn flag, a pure chase gate, and a reflective helper that pushes a low-level `CameraHint.ChaseTarget` (the same `PlanarScrollCamera.Chase` path the native camera uses) at each replay site. Explosion-VFX and fire/damage ordering are verify-first: built only if the in-game gate shows them broken.

**Tech Stack:** C# / .NET (net472), Harmony (`HarmonyLib.AccessTools`), Unity (`UnityEngine`), xUnit. Host-authoritative; client sim frozen ("mirror").

**Spec:** `docs/superpowers/specs/2026-06-27-inc3-tactical-combat-camera-design.md`

**Pre-req:** Inc2 (Facing) is committed (`74b462c`), tree clean. Build/test from repo root `E:\DEV\PhoenixPoint\Multipleer`:
- Build: `dotnet build Multipleer.csproj`
- Test: `dotnet test Multipleer.Tests\Multipleer.Tests.csproj`

**Shared-tree discipline:** A parallel session may touch tactical files. Before each commit run `git status --short`; stage ONLY the files named in the task, BY NAME (never `git add -A`). If build/test fails in files you did not touch, that is a concurrent edit — report, do not fix.

---

## File Structure

- `src/Sync/Tactical/ClientEnemyTurnCameraGate.cs` — NEW. Pure Unity-free policy: chase only on enemy turn + resolved actor. Mirrors `ClientEnemyTurnPresentationGate`.
- `Multipleer.Tests/ClientEnemyTurnCameraGateTests.cs` — NEW. xUnit tests for the gate.
- `src/Sync/Tactical/TacticalEnemyTurnCamera.cs` — NEW. Reflective engine helper: push `CameraHint.ChaseTarget` for an actor (follow transform or snap to pos). One responsibility: the chase call.
- `src/Sync/Tactical/TacticalTurnSync.cs` — MODIFY. Add `IsClientEnemyTurn` static flag, set in `ClientOnTurn`.
- `src/Sync/Tactical/TacticalDeploySync.cs` — MODIFY. Clear the flag in `OnMissionExit`.
- `src/Sync/Tactical/TacticalFireAnimSync.cs` — MODIFY. Inject chase (shooter) in `ClientOnFireStart`.
- `src/Sync/Tactical/TacticalMeleeAnimSync.cs` — MODIFY. Inject chase (attacker) in `ClientOnMeleeStart`.
- `src/Sync/Tactical/TacticalMoveSync.cs` — MODIFY. Inject chase (follow actor) in `ClientOnMoveStart`.
- (Conditional) explosion-VFX + ordering patches — only if Task 5 proves them broken.
- (Finalize) `docs/COOP-SYNC-ROADMAP.md`, memory `multipleer-mod-status.md`, stale "stub" comments.

> Line numbers below are post-Inc2-commit anchors from grounding. RE-CONFIRM each by reading the file before editing — a concurrent session may have shifted them. Locate by the quoted surrounding code, not the bare number.

---

## Task 1: Enemy-turn flag + clear-on-exit

**Files:**
- Modify: `src/Sync/Tactical/TacticalTurnSync.cs` (static field + set in `ClientOnTurn`, method lines ~122-297; `isPlayer` resolved at ~:187)
- Modify: `src/Sync/Tactical/TacticalDeploySync.cs` (`OnMissionExit`, lines ~634-664; external resets ~:656-663)

No unit test — these are field writes inside reflective lifecycle methods (covered by build-green + the Task 5 in-game gate).

- [ ] **Step 1: Add the static flag to `TacticalTurnSync`**

Find the other static fields near the top of `class TacticalTurnSync` (e.g. next to the replay/state statics) and add:

```csharp
/// <summary>True while the client is presenting a non-player (enemy) faction turn on the
/// mirror. Drives the Inc3 enemy-turn cinematic camera (see TacticalEnemyTurnCamera). Set in
/// ClientOnTurn from the incoming faction; cleared on mission exit.</summary>
public static bool IsClientEnemyTurn;
```

- [ ] **Step 2: Set the flag in `ClientOnTurn`**

In `ClientOnTurn`, find where the incoming faction's player-ownership is resolved:

```csharp
bool isPlayer = ToBool(GetProp(current, "IsControlledByPlayer"));
```

Immediately AFTER that line add:

```csharp
IsClientEnemyTurn = !isPlayer;
```

- [ ] **Step 3: Clear the flag on mission exit**

In `TacticalDeploySync.OnMissionExit`, following the existing isolated-try/catch reset pattern (each external reset is its own `try { ... } catch { }`), add near the other resets (~:661):

```csharp
try { TacticalTurnSync.IsClientEnemyTurn = false; } catch { }
```

- [ ] **Step 4: Build**

Run: `dotnet build Multipleer.csproj`
Expected: Build succeeded, 0 Error, 0 Warning.

- [ ] **Step 5: Commit**

```bash
git status --short
git add "src/Sync/Tactical/TacticalTurnSync.cs" "src/Sync/Tactical/TacticalDeploySync.cs"
git commit -m "feat(multipleer-tac): client enemy-turn flag for cinematic camera [Inc3]"
```

---

## Task 2: Pure chase gate (TDD)

**Files:**
- Create: `src/Sync/Tactical/ClientEnemyTurnCameraGate.cs`
- Test: `Multipleer.Tests/ClientEnemyTurnCameraGateTests.cs`

Model EXACTLY on the sibling `ClientEnemyTurnPresentationGate.cs` + `ClientEnemyTurnPresentationGateTests.cs` (same namespace, usings, doc-comment style).

- [ ] **Step 1: Write the failing tests**

Create `Multipleer.Tests/ClientEnemyTurnCameraGateTests.cs`. Match the `using`/`namespace` header of `ClientEnemyTurnPresentationGateTests.cs` exactly, then:

```csharp
public class ClientEnemyTurnCameraGateTests
{
    [Fact]
    public void EnemyTurn_ActorResolved_Chases()
        => Assert.True(ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(isClientEnemyTurn: true, actorResolved: true));

    [Fact]
    public void PlayerTurn_DoesNotChase()
        => Assert.False(ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(isClientEnemyTurn: false, actorResolved: true));

    [Fact]
    public void EnemyTurn_ActorUnresolved_DoesNotChase()
        => Assert.False(ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(isClientEnemyTurn: true, actorResolved: false));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Multipleer.Tests\Multipleer.Tests.csproj`
Expected: FAIL — `ClientEnemyTurnCameraGate` does not exist (compile error).

- [ ] **Step 3: Write the gate**

Create `src/Sync/Tactical/ClientEnemyTurnCameraGate.cs` (use the namespace from the sibling gate, i.e. `Multipleer.Sync.Tactical`):

```csharp
namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// Pure policy for the client enemy-turn cinematic camera: chase an actor only when the
    /// client is presenting an enemy (non-player) turn AND the actor resolved. Unity-free so it
    /// is unit-tested; the engine-side chase lives in <see cref="TacticalEnemyTurnCamera"/>.
    /// </summary>
    public static class ClientEnemyTurnCameraGate
    {
        public static bool ShouldChaseEnemyAction(bool isClientEnemyTurn, bool actorResolved)
            => isClientEnemyTurn && actorResolved;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Multipleer.Tests\Multipleer.Tests.csproj`
Expected: PASS — all existing tests + 3 new green.

- [ ] **Step 5: Commit**

```bash
git status --short
git add "src/Sync/Tactical/ClientEnemyTurnCameraGate.cs" "Multipleer.Tests/ClientEnemyTurnCameraGateTests.cs"
git commit -m "feat(multipleer-tac): pure ClientEnemyTurnCameraGate.ShouldChaseEnemyAction [Inc3]"
```

---

## Task 3: Reflective camera-chase helper

**Files:**
- Create: `src/Sync/Tactical/TacticalEnemyTurnCamera.cs`

Engine glue (reflection against game camera types) — no unit test; covered by build-green + Task 5 in-game gate. Grounded API: `Base.Cameras.CameraChaseParams` (fields `ChaseTransform:Transform`, `ChaseVector:Vector3`, `SnapToFloorHeight:bool`, `ChaseOnlyOutsideFrame:bool`); `Base.Cameras.CameraHint.ChaseTarget`; `Base.Cameras.CameraDirector.Hint(CameraHint, object)` reached via `TacticalDeploySync.LiveTlc` → `View` → `CameraDirector`; actor world pos via the `Pos` property; actor `Transform` via `Component.transform`.

- [ ] **Step 1: Write the helper**

Create `src/Sync/Tactical/TacticalEnemyTurnCamera.cs`:

```csharp
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// Client-side enemy-turn cinematic camera. On the co-op mirror the enemy replay coroutines
    /// (move/fire/melee) bypass TacticalAbility.Activate, so the native
    /// CameraDirector.Hint(AbilityActivated) never fires and the camera never follows enemy
    /// actions. This pushes a low-level CameraHint.ChaseTarget at each replay site (gated by
    /// ClientEnemyTurnCameraGate on TacticalTurnSync.IsClientEnemyTurn), driving the same
    /// PlanarScrollCamera.Chase path the native camera uses
    /// (CameraDirector.Hint(CameraHint, object) -> CameraManager -> HandleHint -> Chase).
    /// Best-effort: any reflection failure is swallowed and never breaks the mirror.
    /// </summary>
    public static class TacticalEnemyTurnCamera
    {
        private static bool _resolved;
        private static bool _resolveFailed;
        private static Type _chaseParamsType;
        private static object _chaseTargetHint;   // boxed CameraHint.ChaseTarget
        private static MethodInfo _directorHint;  // CameraDirector.Hint(CameraHint, object)
        private static FieldInfo _fChaseTransform;
        private static FieldInfo _fChaseVector;
        private static FieldInfo _fSnapToFloor;
        private static FieldInfo _fOnlyOutsideFrame;

        private static void EnsureResolved()
        {
            if (_resolved || _resolveFailed) return;
            try
            {
                _chaseParamsType = AccessTools.TypeByName("Base.Cameras.CameraChaseParams");
                Type hintType = AccessTools.TypeByName("Base.Cameras.CameraHint");
                Type directorType = AccessTools.TypeByName("Base.Cameras.CameraDirector");
                if (_chaseParamsType == null || hintType == null || directorType == null)
                    throw new Exception("camera types not found");

                _chaseTargetHint = Enum.Parse(hintType, "ChaseTarget");
                _directorHint = AccessTools.Method(directorType, "Hint", new[] { hintType, typeof(object) });
                _fChaseTransform = AccessTools.Field(_chaseParamsType, "ChaseTransform");
                _fChaseVector = AccessTools.Field(_chaseParamsType, "ChaseVector");
                _fSnapToFloor = AccessTools.Field(_chaseParamsType, "SnapToFloorHeight");
                _fOnlyOutsideFrame = AccessTools.Field(_chaseParamsType, "ChaseOnlyOutsideFrame");
                if (_directorHint == null || _fChaseVector == null)
                    throw new Exception("camera members not found");

                _resolved = true;
            }
            catch (Exception e)
            {
                _resolveFailed = true;
                Debug.LogWarning("[Multipleer][tac] enemy-turn camera resolve failed: " + e.Message);
            }
        }

        /// <summary>Chase the actor. follow=true tracks the live transform (moves); follow=false
        /// snaps once to the actor's current position (shot/melee).</summary>
        public static void ChaseActor(object actor, bool follow)
        {
            if (actor == null) return;
            EnsureResolved();
            if (_resolveFailed) return;
            try
            {
                object director = GetProp(GetProp(TacticalDeploySync.LiveTlc, "View"), "CameraDirector");
                if (director == null) return;

                object p = Activator.CreateInstance(_chaseParamsType);
                _fSnapToFloor?.SetValue(p, true);
                _fOnlyOutsideFrame?.SetValue(p, true);

                if (follow)
                {
                    Transform tr = (actor as Component)?.transform;
                    if (tr == null) return;
                    _fChaseTransform?.SetValue(p, tr);
                }
                else
                {
                    _fChaseVector.SetValue(p, GetPos(actor));
                }

                _directorHint.Invoke(director, new object[] { _chaseTargetHint, p });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multipleer][tac] enemy-turn camera chase failed: " + e.Message);
            }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo pi = AccessTools.Property(obj.GetType(), name);
            if (pi != null) return pi.GetValue(obj, null);
            FieldInfo fi = AccessTools.Field(obj.GetType(), name);
            return fi?.GetValue(obj);
        }

        private static Vector3 GetPos(object actor)
        {
            object p = GetProp(actor, "Pos");
            return p is Vector3 v ? v : Vector3.zero;
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Multipleer.csproj`
Expected: Build succeeded, 0 Error, 0 Warning. (If `Base.Cameras.*` type names mismatch, re-confirm against `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src\Base.Cameras\` and fix the `TypeByName` strings.)

- [ ] **Step 3: Run tests (no regression)**

Run: `dotnet test Multipleer.Tests\Multipleer.Tests.csproj`
Expected: PASS — full suite green.

- [ ] **Step 4: Commit**

```bash
git status --short
git add "src/Sync/Tactical/TacticalEnemyTurnCamera.cs"
git commit -m "feat(multipleer-tac): reflective enemy-turn camera chase helper [Inc3]"
```

---

## Task 4: Inject chase at the three replay sites

**Files:**
- Modify: `src/Sync/Tactical/TacticalFireAnimSync.cs` (`ClientOnFireStart` ~:133-188; shooter ~:155, target ~:165)
- Modify: `src/Sync/Tactical/TacticalMeleeAnimSync.cs` (`ClientOnMeleeStart` ~:97-153; attacker ~:105, BashCrt ~:131)
- Modify: `src/Sync/Tactical/TacticalMoveSync.cs` (`ClientOnMoveStart` ~:257-301; actor ~:267, dst ~:270)

Engine glue — no unit test; build-green + Task 5 gate. Each guard uses the Task 2 gate + Task 1 flag.

- [ ] **Step 1: Inject in `ClientOnFireStart`**

In `TacticalFireAnimSync.ClientOnFireStart`, after the shooter and target are resolved (after the line that builds the synced fire target, before the `tlc` fetch), add:

```csharp
if (ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(TacticalTurnSync.IsClientEnemyTurn, shooter != null))
    TacticalEnemyTurnCamera.ChaseActor(shooter, follow: false);
```

(Use the actual shooter variable name in scope — grounding shows `object shooter = TacticalDeploySync.ResolveLiveActor(s.ShooterNetId);`.)

- [ ] **Step 2: Inject in `ClientOnMeleeStart`**

In `TacticalMeleeAnimSync.ClientOnMeleeStart`, after the attacker is resolved and before the `BashCrt` method is invoked, add:

```csharp
if (ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(TacticalTurnSync.IsClientEnemyTurn, attacker != null))
    TacticalEnemyTurnCamera.ChaseActor(attacker, follow: false);
```

(Attacker var from grounding: `object attacker = TacticalDeploySync.ResolveLiveActor(s.AttackerNetId);`.)

- [ ] **Step 3: Inject in `ClientOnMoveStart`**

In `TacticalMoveSync.ClientOnMoveStart`, after the actor is resolved and the destination `dst` is built (before the nav branch), add a FOLLOW chase so the camera tracks the walk:

```csharp
if (ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(TacticalTurnSync.IsClientEnemyTurn, actor != null))
    TacticalEnemyTurnCamera.ChaseActor(actor, follow: true);
```

(Actor var from grounding: `object actor = TacticalDeploySync.ResolveLiveActor(s.NetId);`.)

- [ ] **Step 4: Build + test**

Run: `dotnet build Multipleer.csproj`
Expected: Build succeeded, 0 Error, 0 Warning.
Run: `dotnet test Multipleer.Tests\Multipleer.Tests.csproj`
Expected: PASS — full suite green.

- [ ] **Step 5: Commit**

```bash
git status --short
git add "src/Sync/Tactical/TacticalFireAnimSync.cs" "src/Sync/Tactical/TacticalMeleeAnimSync.cs" "src/Sync/Tactical/TacticalMoveSync.cs"
git commit -m "feat(multipleer-tac): chase camera on enemy move/fire/melee replay [Inc3]"
```

---

## Task 5: In-game acceptance gate (MANUAL — user-run, 2 instances)

Not automatable. The user runs it; the implementer prepares the build and the checklist.

- [ ] **Step 1: Deploy**

Run: `.\deploy.ps1` (deploys to the game Mods dir; junction covers the 2nd instance). Restart BOTH instances (client via `tools\launch-second-copy.bat`), host + client, DirectIP `127.0.0.1`.

- [ ] **Step 2: Camera checks (WS1)**

Watch `[Multipleer][tac]` logs. On the ENEMY turn, the CLIENT camera must:
- Follow an enemy that MOVES (camera tracks the walk).
- Snap to an enemy that SHOOTS (before/at the shot animation).
- Snap to an enemy that MELEES.
- Player-turn camera unaffected (no hijack of the local player's camera).

- [ ] **Step 3: Combat replication checks (WS4)**

- Single shot = 1 hit; burst = N hits; grenade AoE = correct per-target damage.
- Melee swings + animates + damages exactly once (melee-F, previously unverified).
- No double damage; death works; shooter AP/WP correct after.

- [ ] **Step 4: Observe WS2 / WS3 (records the conditional triggers)**

- WS2: does the grenade EXPLOSION VFX (blast/smoke/fire at the detonation point) play on the CLIENT? (Projectile flies regardless — watch the impact.)
- WS3: does any impact/HP-drop appear BEFORE the shot animation on the client?

- [ ] **Step 5: Record outcome**

Write pass/fail per check. If all WS1+WS4 pass and WS2 VFX shows + WS3 ordering correct → skip Tasks 6-7, go to Task 8. Otherwise enable the relevant conditional task.

---

## Task 6 (CONDITIONAL — only if Task 5 Step 4 shows the explosion VFX is MISSING on the client)

**Files:**
- Modify: `src/Harmony/Tactical/FireAnimSyncPatches.cs` (`ProjectileDamageNeuterPatch`, `WaitForProjectilesNeuterPatch`)
- Possibly Create: a patch on `ProjectileLogic.OnTrajectoryEnd` (detonation-VFX path)

- [ ] **Step 1: Ground the detonation-VFX path**

Read `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src\...\ProjectileLogic.cs`: find `OnTrajectoryEnd` / `AffectTarget` and identify where the explosion VFX (particle/blast spawn) is triggered vs where damage (`AffectTarget`) is applied. Determine whether the current neuter chain (`ProjectileDamageNeuterPatch` skipping `AffectTarget`, `WaitForProjectilesNeuterPatch` returning empty) clips the VFX spawn.

- [ ] **Step 2: Let VFX through without damage**

Adjust the neuter so the VFX spawn runs while damage stays suppressed: narrow `ProjectileDamageNeuterPatch` to skip ONLY the damage application (not the VFX spawn), or add a client-only patch that invokes the detonation-VFX spawn at `OnTrajectoryEnd`. Exact code is finalized against Step 1's findings (the precise method to allow is unknown until the path is read — this task only runs once the in-game gate proves the VFX is clipped).

- [ ] **Step 3: Build, deploy, re-verify in-game**

Run: `dotnet build Multipleer.csproj` → 0/0. Re-run Task 5 Step 4: explosion VFX now shows, damage still single, no double-apply.

- [ ] **Step 4: Commit**

```bash
git status --short
git add "src/Harmony/Tactical/FireAnimSyncPatches.cs"  # + any new patch file
git commit -m "fix(multipleer-tac): replay grenade explosion VFX on client without damage [Inc3]"
```

---

## Task 7 (CONDITIONAL — only if Task 5 Step 4 shows impact/damage BEFORE the shot animation)

**Files:**
- Modify: `src/Sync/Tactical/TacticalFireAnimSync.cs` and/or `src/Sync/Tactical/TacticalActorStateSync.cs`

- [ ] **Step 1: Confirm the mis-ordering**

From the in-game observation + `[Multipleer][tac]` logs, confirm a target's HP/impact applies before its incoming `tac.fire.start` animation begins (the roadmap-flagged `fire-start vs damage-delta` race).

- [ ] **Step 2: Defer the damage delta until anim start**

Add an apply-order guard: when a `tac.fire.start` for a target is pending/active, defer applying that target's incoming health/damage delta until the fire animation has started (buffer + flush on anim start). Exact seam (where to buffer) is finalized against the confirmed ordering in Step 1.

- [ ] **Step 3: Build, deploy, re-verify**

Run: `dotnet build Multipleer.csproj` → 0/0; `dotnet test Multipleer.Tests\Multipleer.Tests.csproj` → green. Re-run Task 5: animation precedes damage.

- [ ] **Step 4: Commit**

```bash
git status --short
git add "src/Sync/Tactical/TacticalFireAnimSync.cs" "src/Sync/Tactical/TacticalActorStateSync.cs"
git commit -m "fix(multipleer-tac): order fire animation before damage delta on client [Inc3]"
```

---

## Task 8: Finalize (docs + memory + stale-comment cleanup)

**Files:**
- Modify: `docs/COOP-SYNC-ROADMAP.md` (mark Inc3 status)
- Modify (memory): `multipleer-mod-status.md`
- Modify: `src/Sync/Tactical/TacticalLiveCodec.cs:458`, `src/Sync/Tactical/TacticalSyncSurfaces.cs:110` (drop stale melee "STUB" wording — melee is code-complete)

- [ ] **Step 1: Update the roadmap**

In `docs/COOP-SYNC-ROADMAP.md`, mark Inc3 per the in-game outcome: DONE (camera + combat verified; WS2/WS3 either verified-clean or fixed). Note any deferral (terrain destruction → Inc5).

- [ ] **Step 2: Clean stale "stub" comments**

At `TacticalLiveCodec.cs:458` and `TacticalSyncSurfaces.cs:110`, replace the melee "STUB" wording with an accurate note (melee replays the full `BashCrt`). Re-confirm the exact lines first (these were earlier flagged as overlapping with Inc2 edits).

- [ ] **Step 3: Update memory status**

Update the `multipleer-mod-status.md` memory: Inc3 DONE, list what shipped (enemy-turn camera; melee in-game verified; explosion-VFX/ordering verified or fixed).

- [ ] **Step 4: Build + test + commit**

Run: `dotnet build Multipleer.csproj` → 0/0; `dotnet test Multipleer.Tests\Multipleer.Tests.csproj` → green.

```bash
git status --short
git add "docs/COOP-SYNC-ROADMAP.md" "src/Sync/Tactical/TacticalLiveCodec.cs" "src/Sync/Tactical/TacticalSyncSurfaces.cs"
git commit -m "docs(multipleer-tac): mark Inc3 done + drop stale melee stub comments [Inc3]"
```

---

## Self-Review (against the spec)

- **Spec coverage:** WS1 camera → Tasks 1-4 (+5 verify). WS2 explosion VFX → Task 5 Step 4 verify + conditional Task 6. WS3 ordering → Task 5 Step 4 verify + conditional Task 7. WS4 acceptance → Task 5. Finalize/deferred-cleanup → Task 8. All spec sections mapped.
- **Type consistency:** `ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(bool, bool)` defined in Task 2, called identically in Task 4. `TacticalEnemyTurnCamera.ChaseActor(object, bool)` defined in Task 3, called in Task 4. `TacticalTurnSync.IsClientEnemyTurn` defined in Task 1, read in Task 4.
- **Conditional tasks:** Tasks 6-7 are genuinely runtime-gated (depend on the in-game observation); their final code is grounded at execution after Task 5, by design (verify-first). This is intentional, not a placeholder.
- **Notes:** Camera chase is best-effort (swallowed failures) so it can never break the frozen mirror. Flag cleared on mission exit to avoid leaking into the next mission / player turn.
