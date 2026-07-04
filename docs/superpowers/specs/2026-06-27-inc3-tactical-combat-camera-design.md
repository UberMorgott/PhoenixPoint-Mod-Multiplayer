# Inc3 — Combat outcome + explosion VFX + enemy-turn camera (design)

- Date: 2026-06-27
- Status: approved (brainstorming complete) → next: writing-plans
- Track: Tactical full-state spine roadmap, Increment 3
- Predecessor: Inc2 (Facing) — IN PROGRESS in a parallel session; **Inc3 coding must not start until Inc2 is committed (clean tree)** because `TacticalMoveSync.cs` is shared.

## 1. Background / current state

Inc3 is ~70% code-complete already. Grounded state (2026-06-27):

- **`tac.damage` (0x88)** — DONE. Harmony postfix on `TacticalActorBase.ApplyDamage` (`CombatSyncPatches.ApplyDamagePatch`) is the single funnel ALL damage flows through. Fires per-target-per-hit ⇒ grenade/AoE/multi-target replicate by design. `TacticalCombatSync.OnHostApplyDamage` / `HandleDamage`, `_applyingRemote` echo guard.
- **`tac.fire.start` (0x90)** — DONE. `TacticalFireAnimSync` re-timed broadcast at shot-anim start; client drives `FireWeaponAtTargetCrt` directly, projectile flies fully (muzzle/smoke/tracer/SFX), damage neutered via `ProjectileDamageNeuterPatch`, camera silenced via `FireCameraHintGuardPatch`.
- **`tac.melee.start` (0x91)** — DONE (code-complete, **in-game UNVERIFIED**). `TacticalMeleeAnimSync` drives `BashCrt` directly with damage/return-fire/vision/ammo neuter guards (`MeleeAnimSyncPatches`). Roadmap "stub" wording is STALE.
- **Delta Health backstop (`ActorFieldHealth` 0x0020)** — DONE (precursor D), death-safe.
- **Enemy-turn presentation (precursor A, `83b95e6`)** — PARTIAL. Banner + HUD swap via `UIStateOtherFactionTurn` work; **camera does NOT follow enemy actions** (the headline gap).

## 2. Goal

With the client sim frozen, close the remaining Inc3 gaps so the client experiences the host's enemy turn and combat the way single-player does:

1. **Enemy-turn cinematic camera** — camera follows each visible enemy action (move / shot / melee). (NEW build — primary deliverable.)
2. **Grenade explosion VFX on client** — detonation blast/smoke/fire shows on the client. (Verify-first → conditional build.)
3. **fire-start vs damage-delta ordering** — impact/damage never precedes the shot animation. (Verify-first → conditional build.)
4. **In-game acceptance** — 2-instance proof that shot/grenade/melee fully replicate (incl. melee-F, still unverified).

## 3. Scope

**In scope:** the 4 work-streams above.

**Out of scope (per roadmap):**
- Terrain / destructible destruction visuals → Inc5.
- Retiring outcome surfaces → Inc6.
- Muzzle flash — already fixed (`FireProjectile` runs fully on client).
- Stale "stub" melee comment cleanup (`TacticalLiveCodec.cs`, `TacticalSyncSurfaces.cs`) — **deferred**: those files overlap Inc2; do last or separately.

## 4. Why camera Approach B (chase injection)

On the co-op client's enemy turn, the replay coroutines emit **zero** usable camera hints:
- Move: raw `TacticalNavigationComponent.Navigate` (no `Activate`) → no hints.
- Fire: `FireWeaponAtTargetCrt` driven directly; `Shoot`/`ProjectileFired` gated off by `AttackType.Synced`; `ShootingStarted` fires but is no-opped by `FireCameraHintGuardPatch`.
- Melee: `BashCrt` emits no camera hints.

So:
- **A (relax the guard)** — NOT viable: nothing useful to let through except `ShootingStarted` (shots only, no move/melee).
- **B (inject chase)** — CHOSEN: explicitly chase the acting actor at each replay site, gated on enemy-turn context. Uniform across all three action types. Does not touch the existing guard.
- **C (hybrid)** — unnecessary complexity; B covers everything.

## 5. WS1 — Enemy-turn cinematic camera (design)

### Enemy-turn detection
- Add a static flag `TacticalTurnSync.IsClientEnemyTurn`.
- Set `true` in `TacticalTurnSync.ClientOnTurn` enemy branch (incoming faction `!IsControlledByPlayer`); set `false` in the player branch and on mission exit/teardown.
- Rationale: existing replay/relay flags (`_replayDepth`, `_applyingRemote`, `_hostApplyDepth`) track replay MODE, not faction ownership — none answer "is this an enemy action". A dedicated flag is the clean signal.

### Pure decision (unit-tested)
- `ShouldChaseEnemyAction(bool isClientEnemyTurn, ...)` — pure predicate mirroring the codebase's existing pure-gate pattern (e.g. `ClientEnemyTurnPresentationGate`, `DecidePositionApply`). Unit-tested in the test assembly. Keeps the policy decision testable; the engine call stays thin.

### Chase injection (engine glue, reflection-bound)
- Helper that calls the low-level chase: `CameraDirector.Hint(CameraHint.ChaseTarget, CameraChaseParams)` (the `CameraHint` overload → `CameraManager.Hint` → `PlanarScrollCamera.HandleHint` → `Chase()` — the same path the native camera uses).
- `CameraChaseParams`: `ChaseTransform = actor.transform` (follows live position, ideal for moves), `SnapToFloorHeight = true`, `ChaseOnlyOutsideFrame = true` (don't yank the camera if the actor is already on screen). For one-shot fire/melee, `ChaseVector = actor.Pos` is acceptable.
- `CameraHint` / `CameraChaseParams` are game types → bind via reflection (`AccessTools`), consistent with existing mod patterns. No unit test for the reflective call; covered by build-green + in-game gate.

### Injection sites (all gated on `IsClientEnemyTurn`)
1. `TacticalMoveSync.ClientOnMoveStart` — chase the actor transform so the camera follows the walk. **(Shared file with Inc2 — do after Inc2 commit.)**
2. `TacticalFireAnimSync.ClientOnFireStart` — chase the shooter as the shot animation starts.
3. `TacticalMeleeAnimSync.ClientOnMeleeStart` — chase the attacker before `BashCrt`.

### Files touched
- `TacticalTurnSync.cs` (flag + set/clear) — not an Inc2 file.
- New pure gate (small class or static method) + its tests.
- `TacticalFireAnimSync.cs`, `TacticalMeleeAnimSync.cs` (inject) — not Inc2 files.
- `TacticalMoveSync.cs` (inject) — **Inc2 file; sequence last.**
- Possibly a small `TacticalEnemyTurnCamera` helper to hold the reflective chase call (keeps each sync class thin / one responsibility).

## 6. WS2 — Grenade explosion VFX (verify-first → conditional)

- **Verify (in-game):** does the detonation VFX (blast/smoke/fire at impact) play on the client? The projectile flies, but `ProjectileDamageNeuterPatch` (skips `ProjectileLogic.AffectTarget`) + `WaitForProjectilesNeuterPatch` (empty coroutine) may clip the post-impact VFX path.
- **If clipped:** add a patch that lets the detonation VFX fire on the client without re-applying damage (target `ProjectileLogic.OnTrajectoryEnd` / detonation-VFX path while keeping `AffectTarget` neutered). Exact native call to be grounded at implementation time — **only built if the verify step proves the VFX is missing.**

## 7. WS3 — fire-start vs damage-delta ordering (verify-first → conditional)

- **Verify (in-game):** does the client ever show impact/HP-drop BEFORE the shot animation plays? (Roadmap-flagged risk.)
- **If observed:** add an apply-order guard so the damage delta for a target is deferred until that target's incoming fire animation has started. **Only built if the verify step shows mis-ordering.**

## 8. WS4 — In-game acceptance gate (manual, 2-instance, user-run)

Deploy (`deploy.ps1`), restart both instances (DirectIP 127.0.0.1). Verify:
- Single shot = 1 hit; burst = N hits; grenade AoE = correct per-target damage.
- Melee swings + animates + damages exactly once (melee-F, previously unverified).
- No double damage; death works; shooter AP/WP correct after.
- **Camera:** follows enemy move/shot/melee during the enemy turn (WS1 acceptance).
- **VFX:** grenade explosion visible on client (WS2 acceptance / verify).
- **Ordering:** animation precedes damage (WS3 acceptance / verify).
- No regression: player-turn camera unaffected; sim stays frozen; no host-hang.

## 9. Testing strategy

- **Pure gates** (`ShouldChaseEnemyAction`) → xUnit, deterministic.
- **Engine glue** (reflective chase, set/clear flag, optional VFX/ordering patches) → build clean (0 err / 0 warn) + in-game gate.
- **Behaviour** → in-game-gated only (manual 2-instance); no automated coverage possible.

## 10. Sequencing, dependencies, risks

- **Dependency:** Inc3 coding starts after Inc2 is committed (clean tree). `TacticalMoveSync.cs` is the overlap; sequence the move-camera injection last regardless.
- **Order within Inc3:** WS1 camera (fire + melee sites) → WS1 move site (post-Inc2) → in-game verify (WS4 folds in WS2/WS3 verify) → conditional WS2/WS3 builds → final acceptance.
- **Risks:**
  - Chase during a frozen sim must not re-enter suppressed logic — keep the call to the low-level `CameraManager`/`PlanarScrollCamera` chase, not `TacticalAbility.Activate`.
  - `ChaseOnlyOutsideFrame` to avoid jarring re-centres.
  - Reflective binding of `CameraHint`/`CameraChaseParams` — verify member names against the decompile at implementation time.
  - Flag lifecycle: ensure `IsClientEnemyTurn` is cleared on mission exit to avoid leaking into the next mission / player turn.

## 11. Acceptance criteria (definition of done)

- Build: 0 err / 0 warn; full test suite green incl. new pure-gate tests.
- In-game 2-instance: all WS4 checks pass; camera follows enemy actions; grenade explosion VFX shows; animation precedes damage; no regression.
- Roadmap updated (Inc3 DONE) + memory `multiplayer-mod-status.md` updated.
