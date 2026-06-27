# Multipleer Sync Canon Rollout — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans. Steps use `- [ ]` checkboxes.

**Goal:** Converge tactical netcode onto the sync canon, fixing the 3 in-game-observed bugs as canon-conforming increments.
**Architecture:** Host-authoritative; client display-only. 3 layers (intent / one generic versioned actor-state record on 0x67 rail / presentation channel). Increments converge side-rails incrementally; new code conforms to canon only.
**Tech Stack:** C# .NET 4.7.2, Harmony/AccessTools, xUnit. Build `dotnet build Multipleer.csproj -c Release`; tests `dotnet test Multipleer.Tests`; deploy `pwsh -NoProfile -File .\deploy.ps1`.

---

## Canon source (read before any increment)

- `CLAUDE.md` → "Multiplayer sync canon" (the 5 one-line laws).
- `docs/superpowers/specs/2026-06-27-multipleer-sync-canon-design.md` (full design; invariants §4, status contract §5, bug→invariant map §6, migration stance §7).

**Invariants this rollout enforces (design §4):**
1. Host-authoritative — client never simulates; local action = suppress + intent; client is display-only.
2. ONE writer per field — no parallel rails, no split-by-direction.
3. One generic versioned state record — new state rides it; no new ad-hoc surface for what the spine covers.
4. Statuses/effects = inert display-only mirror BY CONTRACT — never live-applied; no allowlist.
5. Presentation separated from state — broadcast to all, initiator-predicted, one synchronized causal beat; outcome applied at impact frame.
6. Everything on the one 0x67 SyncEnvelope rail; ad-hoc parallel surfaces are retired/converged.

**Migration stance (design §7):** new code conforms to the canon only; existing side-rails converge INCREMENTALLY; never rewrite working code wholesale; never add a new mechanism for what the canon already covers.

**Tactical surfaces referenced** (from `src/Sync/Tactical/TacticalSyncSurfaces.cs`): `TacMove=0x83`, `TacMoveStart=0x86`, `TacDamage=0x88`, `TacOverwatchState=0x8D`, `TacActorState=0x8F` (the generic per-actor AP/WP + status-set delta = the spine), `TacFireStart=0x90`, `TacMeleeStart=0x91`.

---

## Increment backlog (ordered)

| # | Increment | Bug | Canon invariant | Status |
|---|-----------|-----|-----------------|--------|
| 1 | Client cover/stance re-derive | A | 3 | **DETAILED below** — ready to execute |
| 2 | Generic status mirror gap + enemy body-part doll | C | 2 + 4 | **DETAILED below** — RCA done, ready to execute |
| 3 | Presentation ordering / simultaneity | B | 5 | Scope only — RCA done; detailed plan written when reached |
| 4 | Converge one-writer-per-field | — | 2 | Scope only — each sub-rail is its own future plan |

Execute strictly in order: increment 1 is a leaf fix with no new surface; 2 and 3 each fix one remaining in-game bug on the existing spine; 4 is pure convergence/refactor (no behavior change) and is safest last.

---

## Increment 1 (DETAILED): Client cover/stance re-derive (bug A)

**Canon invariant served:** 3 (stance/pose re-derived at the generic record's apply on the client). Design §6 bug A: "client re-runs engine's own `GetBestIdleCoverPoseAt` at mirror move-completion; sim-freeze otherwise skips it." Conforms to invariant 1 (display-only, no host sim) and adds **zero new surface** (invariants 3 + 6).

### Root cause (grounded against the live decompile)

On the host, the cover pose is set inside `MoveAbility.Move(PlayingAction)` — the move coroutine — at **`MoveAbility.cs:121`**:

```csharp
base.TacticalActor.IdleAbility.SetIdleParams(base.TacticalActor.TacticalPerception.GetBestIdleCoverPoseAt(target.PositionToApply));
```

A mirroring client **suppresses + reconciles** the move (it never runs `MoveAbility.Move`), so `SetIdleParams` is never called and the animator's `CoverType` int stays `0` → the mirrored soldier never crouches into cover. Fix (wire-free, Option A): re-run the SAME two engine calls by reflection at each client move-completion site, using `searchForEnemy:false` (the client's vision path is frozen — never trigger the enemy search; precedent `CharacterTargetDummy.cs:381` `Perception.GetBestIdleCoverPoseAt(gridPos, searchForEnemy: false)`).

### Verified engine signatures (live decompile `decompiled/AssemblyCSharp`)

| Member | Signature | Source |
|--------|-----------|--------|
| `TacticalActor.IdleAbility` | `public IdleAbility IdleAbility => GetAbility<IdleAbility>();` | `TacticalActor.cs:200` |
| `TacticalActor.TacticalPerception` | `public TacticalPerception TacticalPerception => (TacticalPerception)base.TacticalPerceptionBase;` | `TacticalActor.cs:147` |
| `TacticalPerception.GetBestIdleCoverPoseAt` | `public CoverPose GetBestIdleCoverPoseAt(Vector3 gridPos, bool searchForEnemy = true)` | `TacticalPerception.cs:347` |
| `IdleAbility.SetIdleParams` | `public void SetIdleParams(CoverPose pose)` | `IdleAbility.cs:101` |
| `CoverPose` | `public struct CoverPose` (namespace `PhoenixPoint.Tactical.Levels`) | `CoverPose.cs:8` |

`CoverPose` is both the **return** of `GetBestIdleCoverPoseAt` and the **parameter** of `SetIdleParams`, so the boxed value passes straight through — the helper never has to name or construct `CoverPose`.

### File structure

- **Modify only:** `src/Sync/Tactical/TacticalMoveSync.cs` — add one private reflection helper + wire it at 4 client move-completion sites.
- No new files, no new wire surface, no new usings (`HarmonyLib.AccessTools`, `UnityEngine.Vector3/Debug`, `System.Exception` are already imported, lines 1-10).

### Testing strategy (honest deviation from default TDD)

The RCA found **no clean pure unit surface**: the helper is pure engine reflection (`TacticalActor`/`IdleAbility`/`TacticalPerception` are live game types, absent from the `Multipleer.Tests` assembly which only holds engine-free logic such as `TacticalActorStateDiff`). Per the canon brief, **do NOT fabricate an xUnit test** here. Verification = clean Release build + deploy + an in-game acceptance gate (the soldier visibly crouches at cover on the client + a confirming log line). If a genuinely pure helper later falls out, add a real test then — not before.

> **Edit anchoring:** line numbers below are as of the current file; Step 1 inserts ~30 lines, shifting later sites. **Anchor every edit on the quoted code text** (each quoted block is unique in the file), not on the line number.

---

### Task 1: Cover/stance re-derive helper + 4 client wire-ups (bug A)

**Files:**
- Modify: `src/Sync/Tactical/TacticalMoveSync.cs` (add helper near line 583; wire at ~675, ~343, ~359, ~518-529)

- [ ] **Step 1: Add the `TryRederiveCoverPose` reflection helper**

Insert the new helper immediately AFTER the `TrySetForward` method and BEFORE the `// ─── CLIENT animated-mirror helpers (FIX B)` section comment.

Find this unique anchor (end of `TrySetForward`, ~lines 582-585):

```csharp
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TrySetForward failed: " + ex); return false; }
        }

        // ─── CLIENT animated-mirror helpers (FIX B) ────────────────────────────────────────────────
```

Replace it with (helper inserted between the closing brace and the section comment):

```csharp
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TrySetForward failed: " + ex); return false; }
        }

        // ─── CLIENT: re-derive idle cover/stance pose (bug A) ───────────────────────────────────────

        /// <summary>CLIENT (mirror): re-derive the idle cover/stance pose at <paramref name="finalPos"/> and push it
        /// into the actor's <c>IdleAbility</c>, replacing the engine step the client's move-suppression skips. On the
        /// host this runs inside the <c>MoveAbility.Move</c> coroutine (MoveAbility.cs:121):
        /// <c>TacticalActor.IdleAbility.SetIdleParams(TacticalActor.TacticalPerception.GetBestIdleCoverPoseAt(target.PositionToApply))</c>.
        /// A mirroring client never runs that coroutine (the move is suppressed + reconciled), so the animator
        /// "CoverType" int stays 0 and the soldier never crouches into cover. This calls the SAME engine methods by
        /// reflection with <c>searchForEnemy:false</c> — the client's vision is frozen, so we must NOT touch the enemy
        /// search (the CharacterTargetDummy precedent, CharacterTargetDummy.cs:381). Pose-only + idempotent → safe to
        /// call at every move-completion site. Returns false on a throw / missing member (e.g. an actor with no
        /// IdleAbility) so callers stay fail-open. Signatures: TacticalActor.IdleAbility (TacticalActor.cs:200),
        /// TacticalActor.TacticalPerception (TacticalActor.cs:147), TacticalPerception.GetBestIdleCoverPoseAt(
        /// Vector3,bool=true)→CoverPose (TacticalPerception.cs:347), IdleAbility.SetIdleParams(CoverPose)
        /// (IdleAbility.cs:101).</summary>
        private static bool TryRederiveCoverPose(object actor, Vector3 finalPos)
        {
            try
            {
                if (actor == null) return false;
                object idle = GetProp(actor, "IdleAbility");
                object perception = GetProp(actor, "TacticalPerception");
                if (idle == null || perception == null) return false;

                var getPose = AccessTools.Method(perception.GetType(), "GetBestIdleCoverPoseAt", new[] { typeof(Vector3), typeof(bool) });
                if (getPose == null) { Debug.LogError("[Multipleer][tac] GetBestIdleCoverPoseAt(Vector3,bool) not found"); return false; }
                // searchForEnemy:false — the client's vision path is frozen; never trigger the enemy search here.
                object pose = getPose.Invoke(perception, new object[] { finalPos, false });
                if (pose == null) return false;

                var setParams = AccessTools.Method(idle.GetType(), "SetIdleParams", new[] { pose.GetType() });
                if (setParams == null) { Debug.LogError("[Multipleer][tac] SetIdleParams(CoverPose) not found"); return false; }
                setParams.Invoke(idle, new[] { pose });
                Debug.Log("[Multipleer][tac] CLIENT re-derived cover pose at (" +
                          finalPos.x.ToString("0.0") + "," + finalPos.y.ToString("0.0") + "," + finalPos.z.ToString("0.0") + ")");
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TryRederiveCoverPose failed: " + ex); return false; }
        }

        // ─── CLIENT animated-mirror helpers (FIX B) ────────────────────────────────────────────────
```

- [ ] **Step 2: Build to confirm the helper compiles in isolation**

Run: `dotnet build Multipleer.csproj -c Release`
Expected: `Build succeeded.` with `0 Error(s)` (the helper is private + currently unused — that is fine; no `TreatWarningsAsErrors` in the csproj). If it fails, the reflection/types are wrong — fix before wiring.

- [ ] **Step 3: Wire site 1 — `ReconcileMoveCrt` (deferred move-rail snap, ~line 675)**

Find (unique — `Vector3 after = GetPos(actor);` occurs only here):

```csharp
            try
            {
                TrySetPosition(actor, dst);
                Vector3 after = GetPos(actor);
```

Replace with:

```csharp
            try
            {
                TrySetPosition(actor, dst);
                TryRederiveCoverPose(actor, dst);   // bug A: re-derive cover/stance the client move-suppression skipped
                Vector3 after = GetPos(actor);
```

- [ ] **Step 4: Wire site 2 — `ClientOnMove` early-interrupt snap (~line 343)**

Find (unique — `"reconcile-interrupt"` occurs only here):

```csharp
                if (navigating) TryCancelNavigation(nav);
                bool placed = TrySetPosition(actor, finalPos);
                branch = placed ? "reconcile-interrupt" : "reconcile-interrupt-failed";
```

Replace with:

```csharp
                if (navigating) TryCancelNavigation(nav);
                bool placed = TrySetPosition(actor, finalPos);
                if (placed) TryRederiveCoverPose(actor, finalPos);   // bug A: re-derive cover/stance after the snap
                branch = placed ? "reconcile-interrupt" : "reconcile-interrupt-failed";
```

- [ ] **Step 5: Wire site 3 — `ClientOnMove` not-navigating snap (~line 359)**

Find (unique — this comment + `"reconcile-snap"` occur only here):

```csharp
                // Not navigating (START no-op/degenerate, or already arrived) → snap immediately (exact cell).
                bool placed = TrySetPosition(actor, finalPos);
                branch = placed ? "reconcile-snap" : "reconcile-snap-failed";
```

Replace with:

```csharp
                // Not navigating (START no-op/degenerate, or already arrived) → snap immediately (exact cell).
                bool placed = TrySetPosition(actor, finalPos);
                if (placed) TryRederiveCoverPose(actor, finalPos);   // bug A: re-derive cover/stance after the snap
                branch = placed ? "reconcile-snap" : "reconcile-snap-failed";
```

- [ ] **Step 6: Wire site 4 — `ApplyMirrorPosition` walk + teleport (0x8F delta bridge, ~lines 518-529)**

The walk branch stages the pose at kickoff (`SetIdleParams` only stores the params; the engine applies them when idle resumes after the nav settles — the same cell the host computed). The teleport branch re-derives right after the snap. Return semantics are preserved (true on apply, false otherwise).

Find:

```csharp
                var mode = TacticalActorStateDiff.DecidePositionApply(dist);
                switch (mode)
                {
                    case TacticalActorStateDiff.PositionApplyMode.None:
                        return false;   // already converged → no churn
                    case TacticalActorStateDiff.PositionApplyMode.Walk:
                        if (nav != null && TryAnimatedNavigate(nav, dst)) return true;
                        // Navigate unavailable / threw → snap so the position still converges (correct cell).
                        return TrySetPosition(actor, dst);
                    case TacticalActorStateDiff.PositionApplyMode.Teleport:
                    default:
                        return TrySetPosition(actor, dst);
                }
```

Replace with:

```csharp
                var mode = TacticalActorStateDiff.DecidePositionApply(dist);
                switch (mode)
                {
                    case TacticalActorStateDiff.PositionApplyMode.None:
                        return false;   // already converged → no churn
                    case TacticalActorStateDiff.PositionApplyMode.Walk:
                        if (nav != null && TryAnimatedNavigate(nav, dst)) { TryRederiveCoverPose(actor, dst); return true; }   // bug A: stage cover pose for walk-end
                        // Navigate unavailable / threw → snap so the position still converges (correct cell).
                        if (TrySetPosition(actor, dst)) { TryRederiveCoverPose(actor, dst); return true; }                      // bug A: re-derive after snap
                        return false;
                    case TacticalActorStateDiff.PositionApplyMode.Teleport:
                    default:
                        if (TrySetPosition(actor, dst)) { TryRederiveCoverPose(actor, dst); return true; }                      // bug A: re-derive after snap
                        return false;
                }
```

- [ ] **Step 7: Build clean (all sites wired)**

Run: `dotnet build Multipleer.csproj -c Release`
Expected: `Build succeeded.` with `0 Error(s)`. `TryRederiveCoverPose` is now referenced from 4 sites (no "unused" concern).

- [ ] **Step 8: Deploy to the game Mods folder**

Run: `pwsh -NoProfile -File .\deploy.ps1`
Expected (final line): `Deployed Multipleer to D:\Steam\steamapps\common\Phoenix Point\Mods\Multipleer` (the script rebuilds Release, copies `Multipleer.dll` + `.pdb` + `meta.json`).

- [ ] **Step 9: In-game acceptance gate (manual — this IS the test)**

Two machines, host + client, in a co-op tactical mission.
1. On the CLIENT, command one player soldier to move to a cell adjacent to cover (low or high cover).
2. **PASS:** after the move completes the soldier visibly **crouches / adopts the cover stance on the client screen**, matching the host (before the fix it stood upright).
3. **Log confirm (client):** `%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Player.log` contains `[Multipleer][tac] CLIENT re-derived cover pose at (...)` for that move, and NO `TryRederiveCoverPose failed` / `not found` errors.
4. **Regression:** a move to open ground → soldier stands (no cover), no errors, no double-animation, and the final cell still matches the host (position convergence unchanged).

If the gate fails, stop and run `superpowers:systematic-debugging` before editing further (re-ground signatures; check the actor is a `TacticalActor` with an `IdleAbility`).

- [ ] **Step 10: Commit (local only — workspace rule: never push during dev)**

```bash
git -C E:/DEV/PhoenixPoint/Multipleer add -A
git -C E:/DEV/PhoenixPoint/Multipleer commit -m "fix(tac): re-derive client cover/stance pose at move completion (bug A)

Client move-suppression skips MoveAbility.Move's
IdleAbility.SetIdleParams(GetBestIdleCoverPoseAt) call, so the animator
CoverType int stays 0 and mirrored soldiers never crouch into cover.
Add TryRederiveCoverPose (reflection, searchForEnemy:false) and invoke it
at every client move-completion site (ReconcileMoveCrt, ClientOnMove
interrupt+snap, ApplyMirrorPosition walk+teleport). Canon invariant 3."
```

(Per the workspace commit-on-green rule, the in-game gate passing + clean build is "green" for this engine-reflection increment; commit immediately, locally.)

---

### Self-review log (increment 1)

- **No placeholders:** every step shows the exact code or exact command + expected output. No TBD/TODO/"handle errors"/"similar to". ✔
- **Types/signatures match real source:** `IdleAbility` (TacticalActor.cs:200), `TacticalPerception` (TacticalActor.cs:147), `GetBestIdleCoverPoseAt(Vector3,bool=true)→CoverPose` (TacticalPerception.cs:347), `SetIdleParams(CoverPose)` (IdleAbility.cs:101), `CoverPose` struct (CoverPose.cs:8) — all read from the live decompile, not memory. The pass-through of the boxed `CoverPose` is type-correct (same struct in/out). ✔
- **Helper-name consistency:** `TryRederiveCoverPose(object, Vector3)` is referenced identically at all 4 sites and in the commit body. ✔
- **Anchors unique:** each find-block was verified unique in `TacticalMoveSync.cs` (`Vector3 after = GetPos(actor);`, `"reconcile-interrupt"`, the not-navigating comment + `"reconcile-snap"`, the `switch (mode)` block). ✔
- **Edit-order safety:** build only gates at Step 2 (helper alone compiles, no `TreatWarningsAsErrors`) and Step 7 (all wired). ✔
- **No new usings needed:** `AccessTools`/`Vector3`/`Debug`/`Exception` already imported. ✔

---

## Increment 2 (DETAILED): Generic status mirror gap + enemy body-part doll (bug C)

**Canon invariants served:** 2 (ONE writer per field — statuses stop riding tac.damage 0x88; the 0x8F spine becomes their sole writer) and 4 (statuses = inert display-only by contract — the magnitude is reconstructed on an inert mirror, never live-applied). Also realizes 3 (statuses already ride the one generic record). Adds **zero new surface**.

### Root cause (grounded against real source — the original "RCA spike" is resolved)

> The original scope guessed bleed was excluded by `IsSyncableStatusType`. **That guess was wrong** — `IsSyncableStatusType` is dead (`TacticalActorStateSync.cs:52` "now unused"); the live host filter is `ShouldMirrorStatus(ReadVisibility(def), typeName)` keyed on healthbar visibility (`TacticalActorStateSync.cs:481`, `:746`), which **does include** BleedStatus. Bleed rides 0x8F today; it just shows the wrong magnitude and the tac.damage path corrupts the apply. Three grounded defects:

1. **Two writers for statuses (violates inv 2).** `tac.damage` (0x88) LIVE-applies statuses on the client: `RebuildDamage` rebuilds a `List<StatusApplication>` and native `ApplyDamage` applies them (`TacticalCombatSync.cs:457-478`), in parallel with the 0x8F inert mirror.
2. **Bleed NRE / addon-tree half-mutation (the §5 corruption).** The wire `DamageStatus` struct carries **no** `StatusTarget` (`TacticalLiveCodec.cs:285`) and `FlattenDamage` drops it (`TacticalCombatSync.cs:394-405` reads only `StatusDef`/`StatusSource`/`Value`). So the rebuilt `StatusApplication.StatusTarget` is null → native sets `Status.Target = null` → `BleedStatus.OnApply` runs LIVE (`Applied=false`, so it enters the `if(!applied)` branch) → `GetTargetSlotName(null)` returns null (`BleedStatus.cs:190-193`) → `BodyState.GetSlot(null)` / `GetSlotBleedValue(null)` NRE (`BleedStatus.cs:133-135,226-229`) — thrown AFTER `OnApply` already subscribed `AddonsManager.AddonDetaching` (`BleedStatus.cs:124`). Partial, never-completed mutation → inconsistent addon tree, and it **aborts the enemy's client `ApplyDamage`** → the enemy body-part doll never gets the limb-HP `StatChangeEvent`. Poison is target-independent (`DamageOverTimeStatus.OnApply`, `.cs:51-88` never touches `Target`) → it survives, hence the asymmetry.
3. **Inert mirror always shows level 0 (magnitude lost).** 0x8F's `ReadStatusValue` carries `Status.Duration` (`TacticalActorStateSync.cs:587-591`), not the magnitude, and the inert seed ZEROES the accumulator (`:976-977`). Display magnitude rode ONLY tac.damage (`StatusApplication.Value`, `TacticalCombatSync.cs:404,471`) — which works for poison (30) but NRE-fails for bleed (0/missing).

### The 3-part canon-conforming fix (generic spine, no new surface)

1. **Stop tac.damage live-applying statuses** — remove the `ApplyStatuses` rebuild (`TacticalCombatSync.cs:457-478`). tac.damage keeps owning HP/armor/stun/death. Removes the null-`Target` Bleed NRE entirely → enemy `ApplyDamage` completes → body-part doll updates (it re-derives from the resulting `StatChangeEvent`; limb HP already rides 0x8F as a backstop). **No doll-specific code is needed** — the doll fix falls out of removing the half-mutation.
2. **0x8F carries display magnitude** — `ReadStatusValue` returns `Status.Value` (not `Duration`). The wire `ActorStatus` already carries `Value` (`TacticalLiveCodec.cs:879-884`), so it round-trips with no codec change; `Signature` already includes `Value` so a magnitude change re-broadcasts (`TacticalActorStateDiff.cs:323-326`).
3. **Inert seed APPLIES the magnitude** — after building+zeroing the inert `_damageAccum`, set its `InitialAmount` from the carried value via a pure mapping: **Bleed → `value`** (`BleedStatus.Value=(int)InitialAmount`), **DoT → `value × DamagePerTurn`** (`DamageOverTimeStatus.Value=InitialAmount/DamagePerTurn`). Set the FIELD directly — never DoT `SetValue` (it `RequestUnapply`s at `IntValue<=0`).

### Verified engine signatures (live decompile + mod source)

| Member | Fact | Source |
|--------|------|--------|
| `BleedStatus.Value` | `=> GetBleedLevel()` = `(int)_damageAccum.InitialAmount` | `BleedStatus.cs:29,110-117` |
| `BleedStatus._damageAccum` | `[SerializeMember] DamageAccumulation` | `BleedStatus.cs:22` |
| `BleedStatus.OnApply` null-`Target` NRE | `GetTargetSlotName(null)`→`GetSlot(null)`/`GetSlotBleedValue` after `AddonDetaching` subscribe | `BleedStatus.cs:124,133-135,190-193,226-229` |
| `DamageOverTimeStatus.Value` | `=> _damageAccum.InitialAmount / DamagePerTurn` | `DamageOverTimeStatus.cs:25` |
| `DamageOverTimeStatus.DamagePerTurn` | `=> DamageEffect.DamageEffectDef.MaximumDamage` | `DamageOverTimeStatus.cs:21` |
| `DamageOverTimeStatus.SetValue` | sets `InitialAmount = value*DamagePerTurn`, RequestUnapplies at `IntValue<=0` | `DamageOverTimeStatus.cs:184-188` |
| `DamageAccumulation.InitialAmount`/`Amount` | settable float fields (existing seed sets both) | `TacticalActorStateSync.cs:976-977` |
| wire `DamageStatus` | `{ string DefGuid; float Value; int SourceNetId; }` — no target | `TacticalLiveCodec.cs:285` |
| wire `ActorStatus` | carries `Value` (round-trips magnitude) | `TacticalLiveCodec.cs:879-884` |

### File structure

- **Test:** `Multipleer.Tests/TacticalActorStateDiffTests.cs` — add 3 facts for the pure magnitude mapping.
- **Modify:** `src/Sync/Tactical/TacticalActorStateDiff.cs` — add the pure helper `StatusMagnitudeToInitialAmount`.
- **Modify:** `src/Sync/Tactical/TacticalActorStateSync.cs` — `ReadStatusValue` → `Status.Value`; thread the carried value through `InvokeApplyStatus` → `SeedInertStatusFields`; apply magnitude.
- **Modify:** `src/Sync/Tactical/TacticalCombatSync.cs` — remove the tac.damage status live-apply.

### Testing strategy

Part 3's magnitude math is a **genuinely pure** function → extract it into the engine-free `TacticalActorStateDiff` module and unit-test it (real xUnit, following the existing `TacticalActorStateDiffTests` patterns). Parts 1, 2 and the seed wiring are engine reflection (not unit-testable without the game) → verified by clean build + full-suite green + the in-game gate.

### Atomicity / migration (must hold)

- **One commit, all parts together.** Parts 1 (remove tac.damage live-apply) and 2+3 (spine carries+applies magnitude) MUST land in the same commit so committed history never has a double-write (0x88 + 0x8F both applying statuses → double-counted magnitude) nor a no-magnitude window.
- **MindControl/Zombified stay HARD-excluded** (faction-flip) via `SurfaceOwnedStatusTypeNames` (`TacticalActorStateDiff.cs:120-125`) — unchanged.
- **Client stays sim-frozen** (enforced by `ClientStatusMirrorGuards`) — unchanged.
- **Vestigial after this fix:** `FlattenDamage`'s status encode (`TacticalCombatSync.cs:394-405`) + the `DamageStatus` wire field still serialize unused status data (harmless — `RebuildDamage` no longer reads it). Their removal is deferred to **increment 4** (one-writer convergence) to keep this fix minimal and avoid touching the wire format.
- **Known residual (flag, out of scope):** `Compute`/`KeyOf` key on `{DefGuid, SourceNetId}` and ignore `Value` (`TacticalActorStateDiff.cs:88-90`), so a magnitude UPDATE to an *already-present* mirrored status (same def+source, e.g. a bleed accumulating) is NOT re-applied on the client — only the INITIAL apply carries magnitude. This fixes the reported bug (status appears with the correct level); live magnitude tracking on an existing icon is a separate future refinement (would need a Value-drift re-apply that does not re-run `OnApply`).

> **Edit anchoring:** line numbers are as of the current files; **anchor every edit on the quoted code** (each quoted block is unique).

---

### Task 2: Status display-magnitude on the spine + retire tac.damage status live-apply (bug C)

**Files:**
- Test: `Multipleer.Tests/TacticalActorStateDiffTests.cs`
- Modify: `src/Sync/Tactical/TacticalActorStateDiff.cs` (add pure helper after `ShouldMirrorStatus`, ~line 138)
- Modify: `src/Sync/Tactical/TacticalActorStateSync.cs` (`ReadStatusValue` ~587; `InvokeApplyStatus` ~792/813/857; `SeedInertStatusFields` ~937/982)
- Modify: `src/Sync/Tactical/TacticalCombatSync.cs` (`RebuildDamage` ~457-478)

- [ ] **Step 1: Write the failing unit tests for the pure magnitude mapping**

Append to `Multipleer.Tests/TacticalActorStateDiffTests.cs`, immediately before the final closing `}` of the class:

```csharp
    // ─── bug C: status display-magnitude → DamageAccumulation.InitialAmount mapping ───────────────────

    [Fact]
    public void Magnitude_Bleed_NoDamagePerTurn_MapsOneToOne()
    {
        // BleedStatus.Value = (int)InitialAmount → InitialAmount = value (Bleed has no DamagePerTurn → pass 0).
        Assert.Equal(20f, TacticalActorStateDiff.StatusMagnitudeToInitialAmount(20f, 0f));
    }

    [Fact]
    public void Magnitude_Dot_ScalesByDamagePerTurn()
    {
        // DamageOverTimeStatus.Value = InitialAmount / DamagePerTurn → InitialAmount = value * DamagePerTurn.
        Assert.Equal(150f, TacticalActorStateDiff.StatusMagnitudeToInitialAmount(30f, 5f));
    }

    [Fact]
    public void Magnitude_NaNOrNonPositiveDamagePerTurn_MapsOneToOne()
    {
        Assert.Equal(12f, TacticalActorStateDiff.StatusMagnitudeToInitialAmount(12f, float.NaN));
        Assert.Equal(12f, TacticalActorStateDiff.StatusMagnitudeToInitialAmount(12f, -3f));
    }
```

- [ ] **Step 2: Run the tests — verify they FAIL (helper does not exist)**

Run: `dotnet test Multipleer.Tests --filter "FullyQualifiedName~Magnitude"`
Expected: FAIL — build error `'TacticalActorStateDiff' does not contain a definition for 'StatusMagnitudeToInitialAmount'`.

- [ ] **Step 3: Add the pure helper**

In `src/Sync/Tactical/TacticalActorStateDiff.cs`, find the end of `ShouldMirrorStatus` (unique anchor):

```csharp
        public static bool ShouldMirrorStatus(int healthBarVisibility, string simpleTypeName = null)
        {
            if (healthBarVisibility == HealthBarVisibilityHidden) return false;
            if (!string.IsNullOrEmpty(simpleTypeName) && SurfaceOwnedStatusTypeNames.Contains(simpleTypeName))
                return false;
            return true;
        }
```

Insert the helper immediately AFTER it:

```csharp
        public static bool ShouldMirrorStatus(int healthBarVisibility, string simpleTypeName = null)
        {
            if (healthBarVisibility == HealthBarVisibilityHidden) return false;
            if (!string.IsNullOrEmpty(simpleTypeName) && SurfaceOwnedStatusTypeNames.Contains(simpleTypeName))
                return false;
            return true;
        }

        /// <summary>PURE magnitude→accumulator mapping for the inert status mirror (bug C). The host's carried
        /// <c>Status.Value</c> is the DISPLAY level; the client seeds <c>DamageAccumulation.InitialAmount</c> so the
        /// mirrored icon shows that level. BleedStatus.Value = (int)InitialAmount → InitialAmount = value (no
        /// DamagePerTurn). DamageOverTimeStatus.Value = InitialAmount / DamagePerTurn → InitialAmount =
        /// value × DamagePerTurn. A NaN / non-positive <paramref name="damagePerTurn"/> (a non-DoT status such as
        /// Bleed, which has no such property) maps 1:1. (BleedStatus.cs:29,116; DamageOverTimeStatus.cs:21,25,184)</summary>
        public static float StatusMagnitudeToInitialAmount(float value, float damagePerTurn)
        {
            if (float.IsNaN(damagePerTurn) || damagePerTurn <= 1e-05f) return value;
            return value * damagePerTurn;
        }
```

- [ ] **Step 4: Run the tests — verify they PASS**

Run: `dotnet test Multipleer.Tests --filter "FullyQualifiedName~Magnitude"`
Expected: PASS — `Passed!  - Failed: 0, Passed: 3`.

> Steps 5-9 are co-dependent engine edits (a new method param + its call sites). Do NOT build between them — the first build is Step 10.

- [ ] **Step 5: Part 2 — `ReadStatusValue` returns `Status.Value`**

In `src/Sync/Tactical/TacticalActorStateSync.cs`, find:

```csharp
        /// <summary>The status' carried value (its <c>Duration</c>, informational — drives the signature so a
        /// duration change re-broadcasts). Best-effort: 0 when unreadable.</summary>
        private static float ReadStatusValue(object status)
        {
            try { object d = GetProp(status, "Duration"); return d != null ? Convert.ToSingle(d) : 0f; }
            catch { return 0f; }
        }
```

Replace with:

```csharp
        /// <summary>The status' carried DISPLAY magnitude — its <c>Status.Value</c> (BleedStatus.Value = (int)bleed
        /// level; DamageOverTimeStatus.Value = level = InitialAmount/DamagePerTurn). Drives the signature (a
        /// magnitude change re-broadcasts) AND is reconstructed on the inert client mirror (bug C: was
        /// <c>Duration</c>, so the mirror always showed level 0; magnitude used to ride tac.damage 0x88, now
        /// retired — canon inv 2). Best-effort: 0 when unreadable. (BleedStatus.cs:29; DamageOverTimeStatus.cs:25)</summary>
        private static float ReadStatusValue(object status)
        {
            try { object v = GetProp(status, "Value"); return v != null ? Convert.ToSingle(v) : 0f; }
            catch { return 0f; }
        }
```

- [ ] **Step 6: Part 3b-i — thread the carried value into the apply call**

In `src/Sync/Tactical/TacticalActorStateSync.cs` (the `diff.ToAdd` loop), find:

```csharp
                    object source = a.SourceNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(a.SourceNetId) : null;
                    if (InvokeApplyStatus(statusComponent, def, source, actor)) addCount++;
```

Replace with:

```csharp
                    object source = a.SourceNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(a.SourceNetId) : null;
                    // a.Value = host Status.Value (display magnitude) → reconstructed on the inert mirror (bug C).
                    if (InvokeApplyStatus(statusComponent, def, source, actor, a.Value)) addCount++;
```

- [ ] **Step 7: Part 3b-ii — add the `value` param to `InvokeApplyStatus` and forward it to the seed**

In `src/Sync/Tactical/TacticalActorStateSync.cs`, find the signature:

```csharp
        private static bool InvokeApplyStatus(object statusComponent, object statusDef, object source, object target)
```

Replace with:

```csharp
        private static bool InvokeApplyStatus(object statusComponent, object statusDef, object source, object target, float value)
```

Then find the seed call inside that method:

```csharp
                SeedInertStatusFields(status, statusDef);
```

Replace with:

```csharp
                SeedInertStatusFields(status, statusDef, value);
```

- [ ] **Step 8: Part 3b-iii — add the `value` param to `SeedInertStatusFields` and apply the magnitude**

In `src/Sync/Tactical/TacticalActorStateSync.cs`, find the signature:

```csharp
        private static void SeedInertStatusFields(object status, object statusDef)
```

Replace with:

```csharp
        private static void SeedInertStatusFields(object status, object statusDef, float value)
```

Then find the end of the `_damageAccum` seed block (unique anchor — the "damageAccum seeded" log and the three closing braces before the `catch`):

```csharp
                            daF.SetValue(status, accum);
                            Debug.Log("[Multipleer][tac] status mirror damageAccum seeded: " + DescribeDef(statusDef));
                        }
                    }
                }
            }
            catch (Exception ex)
```

Replace with (magnitude block inserted after the `_damageAccum` block, still inside the `try`):

```csharp
                            daF.SetValue(status, accum);
                            Debug.Log("[Multipleer][tac] status mirror damageAccum seeded: " + DescribeDef(statusDef));
                        }
                    }
                }

                // BUG C / canon inv 4: APPLY the host's display magnitude to the freshly-seeded accum so the inert
                // mirror shows the right level (was always 0 — magnitude used to ride tac.damage 0x88, now retired).
                // value = host Status.Value: Bleed.Value=(int)InitialAmount, DoT.Value=InitialAmount/DamagePerTurn.
                // Map via the PURE helper, then set the FIELD directly — NEVER DoT SetValue (it RequestUnapply's at
                // IntValue<=0, DamageOverTimeStatus.cs:186-188). DoT discriminated by its DamagePerTurn property
                // (BleedStatus has none → maps 1:1).
                object accumNow = daF != null ? daF.GetValue(status) : null;
                if (accumNow != null && value > 1e-05f)
                {
                    float dpt = 0f;
                    var dptProp = AccessTools.Property(status.GetType(), "DamagePerTurn");
                    if (dptProp != null) { object d = dptProp.GetValue(status, null); if (d != null) dpt = Convert.ToSingle(d); }
                    float initialAmount = TacticalActorStateDiff.StatusMagnitudeToInitialAmount(value, dpt);
                    AccessTools.Field(daF.FieldType, "InitialAmount")?.SetValue(accumNow, initialAmount);
                    AccessTools.Field(daF.FieldType, "Amount")?.SetValue(accumNow, initialAmount);
                    Debug.Log("[Multipleer][tac] status mirror magnitude applied: " + DescribeDef(statusDef) +
                              " value=" + value.ToString("0.##") + " initialAmount=" + initialAmount.ToString("0.##"));
                }
            }
            catch (Exception ex)
```

- [ ] **Step 9: Part 1 — stop tac.damage live-applying statuses**

In `src/Sync/Tactical/TacticalCombatSync.cs` (`RebuildDamage`), find:

```csharp
                object dmgTypeDef = DefReflection.GetDefByGuid(p.DamageTypeDefGuid);
                if (dmgTypeDef != null) SetField(t, ref dr, "DamageTypeDef", dmgTypeDef);

                // ApplyStatuses: rebuild List<StatusApplication> with resolved StatusDef + source actor.
                if (p.Statuses != null && p.Statuses.Count > 0)
                {
                    var statusAppType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.StatusApplication");
                    if (statusAppType != null)
                    {
                        var listType = typeof(List<>).MakeGenericType(statusAppType);
                        var list = (IList)Activator.CreateInstance(listType);
                        foreach (var s in p.Statuses)
                        {
                            object def = DefReflection.GetDefByGuid(s.DefGuid);
                            if (def == null) continue;
                            object sa = Activator.CreateInstance(statusAppType);
                            SetField(statusAppType, ref sa, "StatusDef", def);
                            SetField(statusAppType, ref sa, "Value", s.Value);
                            object src = s.SourceNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(s.SourceNetId) : null;
                            SetField(statusAppType, ref sa, "StatusSource", src);
                            list.Add(sa);
                        }
                        if (list.Count > 0) SetField(t, ref dr, "ApplyStatuses", list);
                    }
                }

                // ActorEffects: rebuild List<EffectDef>.
```

Replace with (drop the whole `ApplyStatuses` rebuild; statuses now ride the 0x8F spine only):

```csharp
                object dmgTypeDef = DefReflection.GetDefByGuid(p.DamageTypeDefGuid);
                if (dmgTypeDef != null) SetField(t, ref dr, "DamageTypeDef", dmgTypeDef);

                // CANON inv 2 + 4 (bug C): tac.damage NO LONGER live-applies statuses on the client. Statuses are
                // host-authoritative DISPLAY state and ride the generic 0x8F spine as INERT mirrors only
                // (TacticalActorStateSync.ReconcileStatuses → InvokeApplyStatus, Applied=true + magnitude seed).
                // Live-applying them here broke one-writer (0x88 + 0x8F both wrote statuses) AND crashed: the wire
                // DamageStatus struct carries no StatusTarget (TacticalLiveCodec.cs:285) and FlattenDamage drops it
                // (TacticalCombatSync.cs:394-405), so the rebuilt StatusApplication.Target was null → native
                // ApplyDamage ran BleedStatus.OnApply LIVE (Applied=false) → GetTargetSlotName(null) →
                // BodyState.GetSlot(null)/GetSlotBleedValue NRE (BleedStatus.cs:133-135,226-229) AFTER it
                // subscribed AddonsManager.AddonDetaching (:124) → §5 half-mutation/addon-tree corruption that also
                // aborted the enemy's client ApplyDamage (no body-part doll). tac.damage keeps owning
                // HP/armor/stun/death only. (FlattenDamage's status encode + the DamageStatus wire field are now
                // vestigial; retiring them is deferred to the inc-4 one-writer convergence.)

                // ActorEffects: rebuild List<EffectDef>.
```

- [ ] **Step 10: Build clean (first build — the co-dependent engine edits are now complete)**

Run: `dotnet build Multipleer.csproj -c Release`
Expected: `Build succeeded.` with `0 Error(s)`. If it fails with "no overload takes 4 arguments" or "no overload takes 5 arguments", a Step 6/7/8 param edit is out of sync — fix and rebuild.

- [ ] **Step 11: Run the FULL test suite — verify green**

Run: `dotnet test Multipleer.Tests`
Expected: `Passed!  - Failed: 0` (includes the 3 new `Magnitude_*` facts; no regressions).

- [ ] **Step 12: Deploy to the game Mods folder**

Run: `pwsh -NoProfile -File .\deploy.ps1`
Expected (final line): `Deployed Multipleer to D:\Steam\steamapps\common\Phoenix Point\Mods\Multipleer`.

- [ ] **Step 13: In-game acceptance gate (manual — the engine-reflection parts)**

Two machines, host + client, in a co-op tactical mission.
1. Inflict a **bleed** and a **poison** on the same enemy on the host (e.g. a bleed-dealing hit + a poison weapon).
2. **PASS (magnitude):** on the CLIENT the enemy's healthbar shows BOTH icons with the host's levels — bleed shows its host value (e.g. **20**, not 0), poison shows its host value (e.g. **30**) — matching the host exactly.
3. **PASS (doll):** the enemy's **body-part doll** on the client reflects the host damage (e.g. damaged arm + torso), no longer blank/stale.
4. **Log confirm (client `Player.log`):** `[Multipleer][tac] status mirror magnitude applied: ... value=20` for the bleed; NO `BleedStatus`/`NullReferenceException` in the status-mirror path; NO `status mirror failed`.
5. **Regression:** no double-counted magnitude (the level matches the host, not 2×); the client stays responsive after the shot (no HUD/camera lockup — the §5 corruption is gone); HP/armor/stun/death still apply via tac.damage as before.

If the gate fails, stop and run `superpowers:systematic-debugging` (re-ground `Status.Value` per type; confirm the status reaches `diff.ToAdd` with a non-zero `Value`).

- [ ] **Step 14: Commit (local only — workspace rule: never push during dev)**

```bash
git -C E:/DEV/PhoenixPoint/Multipleer add -A
git -C E:/DEV/PhoenixPoint/Multipleer commit -m "fix(tac): statuses ride the 0x8F spine only, with display magnitude (bug C)

tac.damage (0x88) live-applied statuses on the client in parallel with the
0x8F inert mirror (two writers). The wire dropped StatusTarget, so a rebuilt
BleedStatus applied LIVE with a null Target -> OnApply NRE after subscribing
AddonDetaching -> addon-tree half-mutation that also aborted the enemy's
client ApplyDamage (no body-part doll). Magnitude rode only tac.damage, so
the inert mirror always showed level 0 (bleed 0/missing).

- Remove the tac.damage status live-apply (RebuildDamage). HP/armor/stun/
  death stay on tac.damage; the NRE/doll-abort is gone.
- 0x8F ReadStatusValue carries Status.Value (display magnitude), not Duration.
- Inert seed applies magnitude: Bleed=value, DoT=value*DamagePerTurn, set on
  DamageAccumulation.InitialAmount directly (never DoT SetValue). Pure mapping
  StatusMagnitudeToInitialAmount + xUnit tests. Canon invariants 2 & 4."
git -C E:/DEV/PhoenixPoint/Multipleer log --oneline -1
```

---

### Self-review log (increment 2)

- **No placeholders:** every step shows exact code or exact command + expected output. ✔
- **Types/signatures match real source:** `BleedStatus.Value`/`_damageAccum` (BleedStatus.cs:29,22,110-117), the null-`Target` NRE chain (BleedStatus.cs:124,133-135,190-193,226-229), `DamageOverTimeStatus.Value`/`DamagePerTurn`/`SetValue` (DamageOverTimeStatus.cs:25,21,184-188), wire `DamageStatus` (TacticalLiveCodec.cs:285) + `ActorStatus.Value` (TacticalLiveCodec.cs:879-884), `ReadStatusValue` (TacticalActorStateSync.cs:587-591), `InvokeApplyStatus`/`SeedInertStatusFields`/`RebuildDamage` anchors all read from source. ✔
- **Method-name + signature consistency:** `StatusMagnitudeToInitialAmount(float,float)` used identically in the test, the helper, and the seed call; `InvokeApplyStatus(...,float value)` and `SeedInertStatusFields(...,float value)` param added at the definition AND every call site (single caller each — verified by grep). ✔
- **Anchors unique:** the `ReadStatusValue` body, the `ToAdd` `InvokeApplyStatus(...)` call, the two method signatures, the `_damageAccum` "damageAccum seeded" close, and the `RebuildDamage` `ApplyStatuses` block were each verified unique in their file. ✔
- **Atomicity honored:** parts 1+2+3 land in ONE commit (Step 14); no build between the co-dependent param edits (single build at Step 10). ✔
- **Pure test surface confirmed:** the magnitude mapping is engine-free → real xUnit tests added; engine-reflection parts gated by build + suite + in-game gate (no fabricated tests). ✔
- **Residual flagged honestly:** magnitude UPDATE to an already-present status is not re-applied (`KeyOf` ignores `Value`) — out of scope, documented. ✔

---

## Increment 3 (SCOPE ONLY): Presentation ordering / simultaneity (bug B)

- **Goal:** outcome (HP via `0x88`/`0x8F`) applies at the presentation's **impact frame**, not before the fire anim (`0x90`); add a per-target impact rendezvous; harden client-local fire prediction; bring melee (`0x91`, currently a stub) onto the same symmetric fire/melee/move presentation pattern.
- **Files:** `src/Sync/Tactical/TacticalFireAnimSync.cs` (0x90), `TacticalMeleeAnimSync.cs` (0x91 stub), `TacticalCombatSync.cs`, the `TacDamage` (0x88) apply path, `TacticalActorStateSync.cs` (0x8F).
- **Canon invariant served:** 5 (presentation separated from state; one synchronized causal beat shot→impact→status; outcome at impact frame; initiator-predicted, never stat-before-anim).
- **RCA: DONE** (root: no cross-surface ordering barrier; `SurfaceSeq` is per-surface monotonic only). **Detailed plan to be WRITTEN when this increment is reached** — it needs a per-target impact-rendezvous design (gate outcome apply on the presentation's impact callback).

## Increment 4 (SCOPE ONLY): Converge one-writer-per-field

- **Goal:** fold the duplicated rails into the 0x8F spine — position **dual-rail** (`0x83`/`0x86` + 0x8F POS), AP/WP **triple-writer** (`0x88` + 0x8F + `ClientApPreserveGate`), health **direction-split** (`0x88` down vs 0x8F up).
- **Files:** `src/Sync/Tactical/TacticalMoveSync.cs` (0x83/0x86), `TacticalActorStateSync.cs` (0x8F spine), `TacticalCombatSync.cs` / damage path (0x88), `ClientApPreserveGate`.
- **Canon invariant served:** 2 (one writer per field; no parallel rails / no split-by-direction) + 6 (retire ad-hoc parallel surfaces).
- **Each sub-rail is its OWN future plan.** Sequence them (position → AP/WP → health) but DO NOT detail here. Each must keep the authoritative owner (death/DoT-damage, AP/WP, overwatch — status contract §6) while the generic mirror drives DISPLAY only; pure convergence, no behavior change → safest done last. No RCA spike needed (audit is the design doc §2 table); each gets a TDD plan when reached.

---

## Execution handoff

Two execution options for the **detailed increments (1 and 2)**:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks (`superpowers:subagent-driven-development`).
2. **Inline Execution** — execute the steps in-session with checkpoints (`superpowers:executing-plans`).

Execute increment 1 (Task 1) then increment 2 (Task 2), in order. Increments 3-4 are NOT ready: 3 needs its detailed plan written (RCA already done), 4 splits into per-rail sub-plans. Do not start 3-4 from this document.
